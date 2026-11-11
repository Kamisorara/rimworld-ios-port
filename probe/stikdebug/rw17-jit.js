// StikDebug launch script for Port17 (RimWorld iOS port probe).
// Pre-allocates the JIT arena that libcoreclr's ExecutableAllocator scans for:
//   1. attach the session PID via debugserver (get_pid() = the app StikDebug
//      spawned with JIT THIS session - select Port17 Probe in the JIT tab!);
//   2. allocate one fixed-size RX arena with the private "_M<size>,rx" packet;
//   3. prepare/touch every page - StikDebug v3.x has a bulk prepare_memory_region
//      JIT call (one invocation); older builds fall back to per-page writes;
//   4. stamp the handshake header at offset 0: magic + pid (little-endian);
//   5. KEEP THE DEBUGGER ATTACHED and resume the app.
//
// DO NOT detach ("D") at the end. Debugger-granted memory does not survive the
// detach - verified on device: the script's own read-back sees the arena, the
// process's vm_region scan no longer does the moment the session detaches.
// StikDebug's own scripts (universal.js/legacy.js) work the same way: the app
// runs while the session stays alive, which is why StikDebug must stay open.
// The runtime side (patches/dualjit-arena.patch -> minipal/Unix/doublemapping.cpp)
// scans ITS OWN process for an RX region starting with the same magic, verifies
// the pid, then creates RX/RW aliases via vm_remap and serves all JIT code from
// it.
(() => {
    const pid = get_pid(), cap = 0x8000000, page = 0x4000;
    log("rw17-jit: session pid = " + pid + " (this must be Port17 Probe!)");
    const stop = send_command("vAttach;" + pid.toString(16));
    if (!/^[TS][0-9a-f]{2}/i.test(stop)) throw Error("Attach failed: " + stop);
    let address = 0, published = false;
    try {
        const r = send_command("_M" + cap.toString(16) + ",rx");
        if (!/^[0-9a-f]+$/i.test(r) || /^E[0-9a-f]{2}$/i.test(r)) throw Error("RX allocation failed: " + r);
        address = parseInt(r,16);
        if (!Number.isSafeInteger(address) || address <= 0 || address % page) throw Error("Invalid allocation");
        log("rw17-jit: arena at 0x" + address.toString(16));

        // Prepare every page so each one is really mapped. Prefer the built-in
        // bulk call (one shot); verify by reading back, else sweep per page.
        let prepared = false;
        if (typeof prepare_memory_region === "function") {
            const pr = prepare_memory_region(address, cap);
            log("rw17-jit: prepare_memory_region -> " + pr);
            const rd = send_command("m" + address.toString(16) + ",4");
            prepared = /^[0-9a-f]{8}$/i.test(rd);
            if (!prepared) log("rw17-jit: bulk prepare unverified (" + rd + "), falling back to page sweep");
        }
        if (!prepared) {
            const total = cap / page;
            for (let i = 0; i < cap; i += page) {
                if (send_command("M"+(address+i).toString(16)+",1:00") !== "OK") throw Error("Unprepared JIT page at offset "+i);
                const n = i / page;
                if (n > 0 && n % 512 === 0) log("rw17-jit: page sweep " + n + "/" + total);
            }
        }

        const bytes = Array.from("RW17-JIT-ARENA1!").map(c=>c.charCodeAt(0));
        for (let shift=0;shift<32;shift+=8) bytes.push((pid>>>shift)&255);
        const tag=bytes.map(b=>b.toString(16).padStart(2,"0")).join("");
        if (send_command("M"+r+",14:"+tag)!=="OK" || send_command("m"+r+",14").toLowerCase()!==tag)
            throw Error("Game arena handshake failed");
        published=true;
    } finally {
        // No "_m" deallocate on success, and NO "D" detach - ever. If something
        // failed above, freeing the arena before detaching is still pointless
        // (it dies with the session anyway), so we just fall through.
    }
    log("rw17-jit: arena prepared (128 MiB RX, pid " + pid + ")");
    log("rw17-jit: keeping the debugger ATTACHED - do not close StikDebug; the probe continues on its own");

    const resumed = (typeof resume_app === "function") ? resume_app() : "no resume_app";
    log("rw17-jit: resume_app -> " + resumed);
    if (typeof resumed === "string" && resumed.indexOf("no app") === 0) {
        // The script ran against an already-running app (no launch session to
        // return to): continue the stopped process without detaching. This call
        // blocks for as long as the app runs - that is expected; keep StikDebug
        // open. The probe now finds the arena and carries on by itself.
        log("rw17-jit: continuing the app (the script will sit here - expected)");
        send_command("c");
    }
})();
