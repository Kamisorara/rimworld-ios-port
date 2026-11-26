#!/usr/bin/env python3
"""Fails if any .dll in the given directories is a .NET reference assembly.

CoreCLR refuses to run a reference assembly (BadImageFormatException, "Reference assemblies cannot be
loaded for execution"). The runtime's collect step hands a few out for libraries it does not build
itself (mscorlib in 0.4.5, System.dll and System.Data.dll in 0.4.41). In the .app's flat root they bind
by name, and whatever binds them dies: a mod assembly that binds `System, Version=4.0.0.0` (0.4.41 on
the device: pawn generation in a new world). host/build-ipa.sh overlays the official facades from
prebuilt/ and runs this on the finished bundle, so the next one is a red build and not a device round.

The check reads the metadata: a CustomAttribute row whose parent is the Assembly row and whose type
is System.Runtime.CompilerServices.ReferenceAssemblyAttribute. Searching the bytes for that name is
wrong: System.Private.CoreLib defines it and every reference assembly's client mentions it.

usage: python3 scripts/check-facades.py <dir> [...]      exit 1 when one is found
"""
import os
import struct
import sys

# Table numbers (ECMA-335 II.22). Only the tables up to CustomAttribute are laid out: the ones read
# are among them, and a table's place depends only on the row counts and the tables before it.
MODULE, TYPEREF, TYPEDEF, FIELD, METHODDEF, PARAM = 0, 1, 2, 4, 6, 8
MEMBERREF, DECLSECURITY, CUSTOMATTRIBUTE, STANDALONESIG = 10, 14, 12, 17
EVENT, PROPERTY, MODULEREF, TYPESPEC = 20, 23, 26, 27
ASSEMBLY, ASSEMBLYREF, FILE, EXPORTEDTYPE, MANIFESTRESOURCE = 32, 35, 38, 39, 40
GENERICPARAM, METHODSPEC, GENERICPARAMCONSTRAINT = 42, 43, 44
STR, GUID, BLOB = "S", "G", "B"
TARGET = ("System.Runtime.CompilerServices", "ReferenceAssemblyAttribute")


def coded(bits, tables):
    return (bits, tuple(tables), None)


def simple(table):
    return (0, None, table)


# Each table: (column, name) pairs in row order. A column is a fixed byte count, a heap, a simple index
# (0, None, table) or a coded index (bits, tables, None); only the tables up to CustomAttribute are laid out.
SCHEMAS = {
    MODULE: [(2, "Generation"), (STR, "Name"), (GUID, "Mvid"), (simple(30), "EncId"), (simple(30), "EncBaseId")],
    TYPEREF: [(coded(2, (MODULE, MODULEREF, ASSEMBLYREF, TYPEREF)), "ResolutionScope"), (STR, "TypeName"), (STR, "TypeNamespace")],
    TYPEDEF: [(4, "Flags"), (STR, "TypeName"), (STR, "TypeNamespace"), (coded(2, (TYPEDEF, TYPEREF, TYPESPEC)), "Extends"), (simple(FIELD), "FieldList"), (simple(METHODDEF), "MethodList")],
    3: [(simple(FIELD), "Field")],
    FIELD: [(2, "Flags"), (STR, "Name"), (BLOB, "Signature")],
    5: [(simple(METHODDEF), "Method")],
    METHODDEF: [(4, "RVA"), (2, "ImplFlags"), (2, "Flags"), (STR, "Name"), (BLOB, "Signature"), (simple(PARAM), "ParamList")],
    7: [(simple(PARAM), "Param")],
    PARAM: [(2, "Flags"), (2, "Sequence"), (STR, "Name")],
    9: [(simple(TYPEDEF), "Class"), (coded(2, (TYPEDEF, TYPEREF, TYPESPEC)), "Interface")],
    MEMBERREF: [(coded(3, (TYPEDEF, TYPEREF, MODULEREF, METHODDEF, TYPESPEC)), "Class"), (STR, "Name"), (BLOB, "Signature")],
    11: [(1, "Type"), (1, "Padding"), (coded(2, (FIELD, PARAM, PROPERTY)), "Parent"), (BLOB, "Value")],
    CUSTOMATTRIBUTE: [
        # HasCustomAttribute, in tag order
        (coded(5, (METHODDEF, FIELD, TYPEREF, TYPEDEF, PARAM, 9, MEMBERREF, MODULE, DECLSECURITY, PROPERTY, EVENT,
                      STANDALONESIG, MODULEREF, TYPESPEC, ASSEMBLY, ASSEMBLYREF, FILE, EXPORTEDTYPE, MANIFESTRESOURCE,
                      GENERICPARAM, GENERICPARAMCONSTRAINT, METHODSPEC)), "Parent"),
        (coded(3, (METHODDEF, MEMBERREF)), "Type"),  # CustomAttributeType: tags 2 (MethodDef) and 3 (MemberRef)
        (BLOB, "Value"),
    ],
}
ASSEMBLY_TAG = 14  # the Assembly's place in HasCustomAttribute


def u16(data, at):
    return struct.unpack_from("<H", data, at)[0]


def u32(data, at):
    return struct.unpack_from("<I", data, at)[0]


def is_reference_assembly(data):
    """None for a file that is not a managed assembly; else whether it has [assembly: ReferenceAssembly]."""
    if len(data) < 0x40 or data[:2] != b"MZ":
        return None
    pe = u32(data, 0x3C)
    if data[pe:pe + 4] != b"PE\0\0":
        return None
    optional = pe + 24
    optional_size = u16(data, pe + 20)
    magic = u16(data, optional)
    if magic not in (0x10B, 0x20B):  # PE32, PE32+
        return None
    cli_rva = u32(data, optional + (96 if magic == 0x10B else 112) + 14 * 8)
    if cli_rva == 0:
        return None
    sections = [struct.unpack_from("<8sIIII", data, optional + optional_size + 40 * i) for i in range(u16(data, pe + 6))]

    def offset(rva):
        for _, virtual_size, virtual_address, raw_size, raw_pointer in sections:
            if virtual_address <= rva < virtual_address + max(virtual_size, raw_size):
                return raw_pointer + rva - virtual_address
        raise ValueError("RVA 0x%x is in no section" % rva)

    metadata = offset(u32(data, offset(cli_rva) + 8))
    streams = {}
    at = metadata + 16 + u32(data, metadata + 12)
    at += 4  # past Flags and Streams: the stream headers follow
    for _ in range(u16(data, at - 2)):
        start, _size = struct.unpack_from("<II", data, at)
        at += 8
        end = data.index(b"\0", at)
        name = data[at:end].decode("ascii")
        at = metadata + ((end + 1 - metadata + 3) & ~3)  # the name is padded to a 4-byte boundary
        streams[name] = metadata + start
    if "#~" not in streams or "#Strings" not in streams:
        raise ValueError("no #~/#Strings stream")

    tables = streams["#~"]
    heap_sizes = data[tables + 6]
    valid = struct.unpack_from("<Q", data, tables + 8)[0]
    at = tables + 24
    rows = [0] * 64
    for table in range(64):
        if valid >> table & 1:
            rows[table] = u32(data, at)
            at += 4
    if heap_sizes & 0x40:
        at += 4  # ExtraData
    heap = {STR: 4 if heap_sizes & 1 else 2, GUID: 4 if heap_sizes & 2 else 2, BLOB: 4 if heap_sizes & 4 else 2}

    def column_size(column):
        if isinstance(column, int):
            return column
        if isinstance(column, str):
            return heap[column]
        bits, coded_tables, table = column
        if bits:
            return 4 if max(rows[t] for t in coded_tables) >= (1 << (16 - bits)) else 2
        return 4 if rows[table] >= 0x10000 else 2

    start, width = {}, {}
    for table in range(CUSTOMATTRIBUTE + 1):
        start[table] = at
        width[table] = sum(column_size(column) for column, _name in SCHEMAS[table])
        at += width[table] * rows[table]

    def read(index_at, size):
        return u16(data, index_at) if size == 2 else u32(data, index_at)

    def string(index):
        if not index:
            return ""
        index_at = streams["#Strings"] + index
        return data[index_at:data.index(b"\0", index_at)].decode("utf-8", "replace")

    def row(table, index):  # metadata index: 1-based, 0 is none
        return start[table] + (index - 1) * width[table] if index else None

    def named(table, index):
        """(namespace, name) of a TypeRef or TypeDef row."""
        row_at = row(table, index)
        if row_at is None:
            return None
        name_at = 4 if table == TYPEDEF else column_size(SCHEMAS[TYPEREF][0][0])
        namespace_at = name_at + heap[STR]
        return string(read(row_at + namespace_at, heap[STR])), string(read(row_at + name_at, heap[STR]))

    def type_named(value, bits):
        """TypeDefOrRef and MemberRefParent agree: tag 0 TypeDef, tag 1 TypeRef."""
        tag, index = value & ((1 << bits) - 1), value >> bits
        return named(TYPEDEF, index) if tag == 0 else named(TYPEREF, index) if tag == 1 else None

    method_list_size = column_size(simple(METHODDEF))
    method_list_at = sum(column_size(column) for column, _name in SCHEMAS[TYPEDEF][:5])

    def method_type(index):
        """The TypeDef whose MethodList range holds the method: the last one that starts at or before it."""
        owner = 0
        for i in range(rows[TYPEDEF]):
            if read(start[TYPEDEF] + i * width[TYPEDEF] + method_list_at, method_list_size) <= index:
                owner = i + 1
        return named(TYPEDEF, owner)

    parent_size = column_size(SCHEMAS[CUSTOMATTRIBUTE][0][0])
    kind_size = column_size(SCHEMAS[CUSTOMATTRIBUTE][1][0])
    for i in range(rows[CUSTOMATTRIBUTE]):
        row_at = start[CUSTOMATTRIBUTE] + i * width[CUSTOMATTRIBUTE]
        parent, kind = read(row_at, parent_size), read(row_at + parent_size, kind_size)
        if parent & 31 != ASSEMBLY_TAG:
            continue
        tag, index = kind & 7, kind >> 3
        if tag == 2 and index:  # MethodDef
            found = method_type(index)
        elif tag == 3 and index:  # MemberRef
            found = type_named(read(row(MEMBERREF, index), column_size(SCHEMAS[MEMBERREF][0][0])), 3)
        else:
            continue
        if found == TARGET:
            return True
    return False


def check(folder):
    flagged, managed = [], 0
    for name in sorted(os.listdir(folder)):
        if not name.lower().endswith(".dll"):
            continue
        path = os.path.join(folder, name)
        if not os.path.isfile(path):
            continue
        with open(path, "rb") as handle:
            data = handle.read()
        try:
            verdict = is_reference_assembly(data)
        except (ValueError, struct.error, IndexError) as e:
            print("  %s: NOT CHECKED: %s" % (name, e))
            flagged.append(name)
            continue
        if verdict is None:
            continue
        managed += 1
        if verdict:
            flagged.append(name)
    print(("%s: REFERENCE ASSEMBLIES in the bundle: %s" % (folder, ", ".join(flagged))) if flagged
          else "%s: %d managed assemblies, none is a reference assembly" % (folder, managed))
    return flagged


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    flagged = []
    for folder in argv[1:]:
        flagged += [os.path.join(folder, name) for name in check(folder)]
    if flagged:
        print("::error::reference assemblies in the bundle (they bind by name and die when loaded): " + ", ".join(flagged))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
