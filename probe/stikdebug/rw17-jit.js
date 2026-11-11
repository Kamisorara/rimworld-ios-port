// StikDebug launch script for Port17 (RimWorld iOS port probe).
// Pre-allocates the JIT arena that libcoreclr's ExecutableAllocator scans for:
//   1. attach the game PID via debugserver;
//   2. allocate one fixed-size RX arena with the private "_M<size>,rx" packet
//      (allowed because the process is CS_DEBUGGED while a debugger attaches);
//   3. touch every page so each one is really mapped (and verified writable);
//   4. stamp the handshake header at offset 0: magic + pid (little-endian);
//   5. detach - the RX arena stays mapped after the debugger leaves.
// The runtime side (patches/dualjit-arena.patch -> minipal/Unix/doublemapping.cpp)
// scans for an RX region starting with the same magic, verifies the pid, then
// creates RX/RW aliases via vm_remap and serves all JIT code from it.
//
// Usage order in StikDebug: enable JIT (debugger attach) -> run this script ->
// launch the app. Magic and capacity must stay in sync with the runtime patch.
(() => {
    const pid = get_pid(), cap = 0x6000000, page = 0x4000;
    const stop = send_command("vAttach;" + pid.toString(16));
    if (!/^[TS][0-9a-f]{2}/i.test(stop)) throw Error("Attach failed: " + stop);
    let address = 0, published = false;
    try {
        const r = send_command("_M" + cap.toString(16) + ",rx");
        if (!/^[0-9a-f]+$/i.test(r) || /^E[0-9a-f]{2}$/i.test(r)) throw Error("RX allocation failed: " + r);
        address = parseInt(r,16);
        if (!Number.isSafeInteger(address) || address <= 0 || address % page) throw Error("Invalid allocation");
        // Each debugserver write must succeed, not just the first page. Write
        // one byte per 16 KiB device page; no code from another process is read.
        const total = cap / page;
        for (let i = 0; i < cap; i += page) {
            if (send_command("M"+(address+i).toString(16)+",1:00") !== "OK") throw Error("Unprepared JIT page at offset "+i);
            const n = i / page;
            if (n > 0 && n % 512 === 0) log("rw17-jit: page sweep " + n + "/" + total);
        }
        const bytes = Array.from("RW17-JIT-ARENA1!").map(c=>c.charCodeAt(0));
        for (let shift=0;shift<32;shift+=8) bytes.push((pid>>>shift)&255);
        const tag=bytes.map(b=>b.toString(16).padStart(2,"0")).join("");
        if (send_command("M"+r+",14:"+tag)!=="OK" || send_command("m"+r+",14").toLowerCase()!==tag)
            throw Error("Game arena handshake failed");
        published=true;
    } finally {
        if (!published && address) send_command("_m"+address.toString(16));
        send_command("D");
    }
    log("rw17-jit: arena prepared (96 MiB RX). Return to the launcher and enable JIT, then launch the app.");
})();
