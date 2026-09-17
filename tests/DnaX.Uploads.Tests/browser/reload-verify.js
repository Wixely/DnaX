// Evaluate after reload-prepare.js and a full page reload.
(async () => {
    const wait = async predicate => { const deadline = Date.now() + 20000; while (!predicate()) {
        if (Date.now() > deadline) throw new Error(document.body.innerText); await new Promise(r => setTimeout(r, 100));
    } };
    const row = name => [...document.querySelectorAll('.dnax-upload-row')].find(r => r.innerText.includes(name));
    await wait(() => row('reload-small.bin') && row('reload-large.bin'));
    const initial = row('reload-large.bin').innerText;
    const originalFetch = window.fetch; const offsets = []; let verifications = 0;
    window.fetch = (url, init) => {
        if (String(url).endsWith('/verify')) verifications++;
        if (String(url).endsWith('/bytes')) offsets.push(Number(init.headers['Upload-Offset']));
        return originalFetch(url, init);
    };
    const select = value => {
        const input = row('reload-large.bin').querySelector('input[type=file]');
        if (!input) throw new Error('Missing reselection control.');
        const transfer = new DataTransfer(); transfer.items.add(new File([new Uint8Array(17 * 1024 * 1024).fill(value)], 'reload-large.bin'));
        input.files = transfer.files; input.dispatchEvent(new Event('change', { bubbles: true }));
    };
    try {
        [...row('reload-small.bin').querySelectorAll('button')].find(b => b.textContent === 'Resume / retry').click();
        select(99);
        await wait(() => row('reload-large.bin').innerText.includes('does not match'));
        select(73);
        await wait(() => row('reload-small.bin').innerText.includes('complete') && row('reload-large.bin').innerText.includes('complete'));
        if (!offsets.length || offsets.some(offset => offset < 4194304) || verifications < 3)
            throw new Error('Previously committed chunks were not correctly verified and reused.');
        return { passed: true, smallFileRecoveredFromStorage: true, largeFileRequiredReselection: initial.includes('selected again'),
            wrongFileRejected: true, verificationRequests: verifications, resumedOffsets: offsets };
    } finally { window.fetch = originalFetch; }
})()
