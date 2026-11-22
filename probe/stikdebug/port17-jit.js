// Port17 JIT session script for the OFFICIAL Microsoft ios-arm64 runtime, on iOS 26+/TXM.
//
// The session model is StikDebug's universal.js one: the script runs against the app
// StikDebug SPAWNED for this session, so vAttach lands before Port17's first instruction —
// CS_DEBUGGED is set for the whole life of the process. No _M arena and no brk
// cooperation is needed: the official runtime's W^X code pages are dual mappings it
// makes itself (mmap RX + vm_remap RW alias), which the kernel honors under CS_DEBUGGED;
// Port17's engine.c proves this in-process (its "JIT 授权检测" line) and switches to
// InterpMode=0 on its own. No jit.on, no env — launching through this session IS the
// whole recipe.
//
// What the loop does: ONE 'c' per stop (the first one resumes the app after vAttach —
// a second continue in flight would answer out-of-turn and kill the session), and every
// genuine signal is RE-DELIVERED with vCont;S (CoreCLR implements managed
// NullReferenceException on SIGSEGV and thread suspension on its own signals — a
// swallowed stop would hang the game mid-play).
(() => {
    const pid = get_pid();
    log("port17-jit: session pid = " + pid + " (must be Port17)");
    const stop = send_command("vAttach;" + pid.toString(16));
    if (!/^[TS][0-9a-f]{2}/i.test(stop)) throw Error("Attach failed: " + stop);
    log("port17-jit: attached — CS_DEBUGGED granted, resuming the app");
    log("port17-jit: KEEP THIS SCREEN OPEN while playing (the session dies with it)");

    for (;;) {
        const resp = send_command("c"); // blocks until the process stops again
        if (!/^[TS][0-9a-f]{2}/i.test(resp)) {
            log("port17-jit: session ended: " + resp);
            break;
        }
        // Where did it stop? Extract thread, pc and the signal number.
        let m = /T[0-9a-f]+thread:([0-9a-f]+);/.exec(resp);
        const tid = m ? m[1] : null;
        m = /20:([0-9a-f]{16});/.exec(resp);
        const pcHex = m ? m[1] : null;
        m = /^T([0-9a-f]{2})/.exec(resp);
        const sig = m ? m[1] : null;
        log("port17-jit: stopped (sig 0x" + sig + ", tid " + tid + ")");

        // A breakpoint instruction (brk) the app itself asked for? The hybrid
        // runtime's JIT26 protocol emits brk #0xf00d handshakes served by
        // rw17-jit26.js; if THIS (official-runtime-style) script is running
        // against a hybrid app, skip the brk (pc+4, plain continue) so the
        // trampoline returns with x0 untouched (NULL) and the host aborts the
        // acquisition cleanly - instead of re-executing the brk forever.
        let isBrk = false;
        let brkTid = null;
        let brkPc = null;
        if (pcHex) {
            // Register values in stop packets are LITTLE-ENDIAN hex (universal.js
            // byte-reverses for a reason): decode before addressing memory.
            const pcLe = parseInt(pcHex.match(/../g).reverse().join(""), 16);
            brkPc = pcLe;
            const instr = send_command("m" + pcLe.toString(16) + ",4");
            if (/^[0-9a-f]+$/i.test(instr)) {
                const u32 = parseInt(instr.match(/../g).reverse().join(""), 16);
                isBrk = ((u32 >>> 0) & 0xFFE0001F) === 0xD4200000;
            }
        }
        if (isBrk && brkPc !== null) {
            log("port17-jit: brk stop at 0x" + brkPc.toString(16) + " - skipping (the hybrid runtime wants rw17-jit26.js for its handshakes)");
            const pcPlus4 = brkPc + 4;
            const bytes = [];
            for (let i = 0; i < 8; i++) { bytes.push(pcPlus4 & 0xff); pcPlus4 /= 256; }
            const le = bytes.map(b => Math.floor(b).toString(16).padStart(2, "0")).join("");
            if (tid) send_command("P20=" + le + ";thread:" + tid + ";");
        }
        // Anything that is not a brk is a real signal: hand it back untouched.
        else if (sig && tid) {
            log("port17-jit: re-delivering signal 0x" + sig);
            send_command("vCont;S" + sig + ":" + tid);
        }
    }
})();
