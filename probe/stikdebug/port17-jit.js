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

        // A breakpoint instruction (brk) the app itself asked for? Not ours — Port17
        // emits none; skip any such stop with a plain continue.
        let isBrk = false;
        if (pcHex) {
            // Register values in stop packets are LITTLE-ENDIAN hex (universal.js
            // byte-reverses for a reason): decode before addressing memory.
            const pcLe = parseInt(pcHex.match(/../g).reverse().join(""), 16);
            const instr = send_command("m" + pcLe.toString(16) + ",4");
            if (/^[0-9a-f]+$/i.test(instr)) {
                const u32 = parseInt(instr.match(/../g).reverse().join(""), 16);
                isBrk = ((u32 >>> 0) & 0xFFE0001F) === 0xD4200000;
            }
        }
        // Anything that is not a brk is a real signal: hand it back untouched.
        if (!isBrk && sig && tid) {
            log("port17-jit: re-delivering signal 0x" + sig);
            send_command("vCont;S" + sig + ":" + tid);
        }
    }
})();
