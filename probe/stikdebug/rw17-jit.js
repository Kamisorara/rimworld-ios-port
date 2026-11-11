// StikDebug launch script for Port17 (RimWorld iOS port probe).
// Modeled 1:1 on STS2-JIT.js from the SlayTheSpire2 community repack (the
// proven reference), with one iOS-26-hardening difference at the end.
//   1. attach the session PID via debugserver (get_pid() = the app StikDebug
//      spawned with JIT this session - or the app you launched this against);
//   2. allocate one fixed-size RX arena with the private "_M<size>,rx" packet;
//   3. write one byte per 16 KiB page (STS2 does exactly this - no bulk API);
//   4. stamp the handshake header at offset 0: magic + pid (little-endian);
//   5. resume the process and KEEP THE SESSION OPEN (universal.js model).
// The runtime side (patches/dualjit-arena.patch -> minipal/Unix/doublemapping.cpp)
// scans its own process for an RX region starting with the same magic, verifies
// the pid, then serves all JIT code from RX/RW aliases of it.
//
// Why no detach ("D") at the end: on one test device (iOS 26) the arena was
// verified present through debugserver, then became invisible to the app's
// own vm_region scan right after the script sent "D" - debugger-granted
// memory only lived while a session was attached. STS2's flow survives its
// detach because a second "enable JIT" attach happens afterwards; the
// universal.js model instead keeps the session alive for the app's lifetime.
// We do the same: the loop below blocks in 'c' (continue) until the app stops
// again, holding the session - and with it the arena - open.
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

        // STS2-exact page preparation: one 1-byte write per 16 KiB page,
        // every write must succeed. ~8192 packets, progress every 1024 pages.
        const total = cap / page;
        for (let i = 0; i < cap; i += page) {
            if (send_command("M"+(address+i).toString(16)+",1:00") !== "OK")
                throw Error("Unprepared JIT page at offset "+i);
            const n = i / page;
            if (n > 0 && n % 1024 === 0) log("rw17-jit: page sweep " + n + "/" + total);
        }

        const bytes = Array.from("RW17-JIT-ARENA1!").map(c=>c.charCodeAt(0));
        for (let shift=0;shift<32;shift+=8) bytes.push((pid>>>shift)&255);
        const tag=bytes.map(b=>b.toString(16).padStart(2,"0")).join("");
        if (send_command("M"+r+",14:"+tag)!=="OK" || send_command("m"+r+",14").toLowerCase()!==tag)
            throw Error("Game arena handshake failed");
        published=true;
        log("rw17-jit: arena prepared (128 MiB RX, pid " + pid + ")");
    } finally {
        // vAttach stopped every thread. ALWAYS put the process back before
        // anything else - a stopped process is the black-screen-no-logs bug
        // of the previous script version. 'c' on an already-running process
        // is harmless (debugserver replies with an error we can ignore).
        try { send_command("c"); } catch (e) { log("rw17-jit: initial c: " + e); }
        if (!published && address) {
            try { send_command("_m"+address.toString(16)); } catch (e) {}
            try { send_command("D"); } catch (e) {}
        }
    }
    // Hold the debug session open (universal.js model): each 'c' blocks until
    // the process stops again, keeping the script - and the arena anchored by
    // the session - alive while the app runs. Keep this window open; closing
    // it ends the session (and, on iOS 26 devices of this kind, the arena).
    log("rw17-jit: holding the session open - KEEP THIS WINDOW OPEN; the probe continues on its own");
    for (;;) {
        const resp = send_command("c");
        if (!/^[TS][0-9a-f]{2}/i.test(resp)) { log("rw17-jit: session ended: " + resp); break; }
        log("rw17-jit: process stopped (" + resp.substring(0,20) + "), continuing");
    }
})();
