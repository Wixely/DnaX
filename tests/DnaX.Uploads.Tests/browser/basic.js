// Evaluate in the sample page using an existing browser MCP tool; no Node runtime.
(async () => {
    const wait = async predicate => {
        const deadline = Date.now() + 20000;
        while (!predicate()) {
            if (Date.now() > deadline) throw new Error('Timed out: ' + document.body.innerText);
            await new Promise(resolve => setTimeout(resolve, 100));
        }
    };
    const roots = [...document.querySelectorAll('.dnax-uploads')];
    await wait(() => roots.every(root => root.querySelector('input[type=file]')));
    const originalFetch = window.fetch;
    const writes = []; let lost = false;
    window.fetch = async (url, init) => {
        if (String(url).endsWith('/bytes') && init?.method === 'PUT') {
            writes.push({ url: String(url), offset: init.headers['Upload-Offset'], bytes: init.body.size });
            const result = await originalFetch(url, init);
            if (!lost && init.headers['Upload-SHA256'] && result.ok) {
                lost = true; throw new TypeError('Simulated lost response after durable acceptance.');
            }
            return result;
        }
        return originalFetch(url, init);
    };
    try {
        // Only this test uses Blazor internals, after verifying the hook exists in the running framework.
        Blazor._internal.forceCloseConnection();
        const select = (root, files) => {
            const transfer = new DataTransfer(); files.forEach(file => transfer.items.add(file));
            const input = root.querySelector('input[type=file]'); input.files = transfer.files;
            input.dispatchEvent(new Event('change', { bubbles: true }));
        };
        const payload = new Uint8Array(9 * 1024 * 1024 + 31); payload.fill(73);
        select(roots[0], [new File([payload], 'chunk-a.bin'), new File(['second file'], 'chunk-b.txt')]);
        select(roots[1], [new File([payload], 'whole-a.bin'), new File(['whole second'], 'whole-b.txt')]);
        select(roots[2], [new File(['minimal'], 'minimal.txt')]);
        await wait(() => roots[0].innerText.includes('2/2 files complete') && roots[1].innerText.includes('2/2 files complete') &&
            roots[2].innerText.includes('1/1 files complete'));
        const byUrl = Object.groupBy(writes, w => w.url);
        const lengths = Object.values(byUrl).map(group => group.map(w => w.bytes));
        if (!lost || !lengths.some(sizes => sizes.length === 3 && sizes[0] === 4194304 && sizes[2] === 1048607))
            throw new Error('Expected resumable chunk transfer was not observed.');
        if (!lengths.some(sizes => sizes.length === 1 && sizes[0] === payload.length))
            throw new Error('Whole-file profile did not use exactly one body.');
        return { passed: true, disconnectedBeforeSelection: true, lostResponseRecovered: true,
            completed: 5, requestBodySizes: lengths, minimalMultiple: roots[2].querySelector('input').multiple };
    } finally { window.fetch = originalFetch; }
})()
