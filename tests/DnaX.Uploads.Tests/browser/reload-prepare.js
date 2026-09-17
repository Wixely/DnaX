// Evaluate in the running sample, then reload it and evaluate reload-verify.js.
(async () => {
    const root = document.querySelector('.dnax-uploads');
    const originalFetch = window.fetch;
    const paused = new Set();
    window.fetch = async (url, init) => {
        if (String(url).endsWith('/bytes') && Number(init?.headers?.['Upload-Offset']) > 0) {
            const id = String(url).split('/').at(-2);
            const row = root.querySelector(`[data-upload-id="${id}"]`);
            if (row?.innerText.includes('reload-') && !paused.has(id)) {
                paused.add(id); [...row.querySelectorAll('button')].find(b => b.textContent === 'Pause').click();
            }
        }
        return originalFetch(url, init);
    };
    try {
        const transfer = new DataTransfer();
        for (const [name, size] of [['reload-small.bin', 9 * 1024 * 1024], ['reload-large.bin', 17 * 1024 * 1024]])
            transfer.items.add(new File([new Uint8Array(size).fill(73)], name));
        const input = root.querySelector('input[type=file]'); input.files = transfer.files;
        input.dispatchEvent(new Event('change', { bubbles: true }));
        const deadline = Date.now() + 20000;
        while (paused.size !== 2 || ![...root.querySelectorAll('.dnax-upload-row')].filter(r => r.innerText.includes('reload-'))
            .every(r => r.innerText.includes('paused'))) {
            if (Date.now() > deadline) throw new Error(root.innerText);
            await new Promise(resolve => setTimeout(resolve, 100));
        }
        await new Promise(resolve => setTimeout(resolve, 500));
        return { passed: true, pausedFiles: 2, rows: [...root.querySelectorAll('.dnax-upload-row')]
            .filter(r => r.innerText.includes('reload-')).map(r => r.innerText) };
    } finally { window.fetch = originalFetch; }
})()
