// Evaluate on the upload sample through an existing browser MCP tool.
(async () => {
    const { mountCustom, canReloadSafely } = await import('/_content/DnaX.Uploads/uploads.js');
    const host = document.createElement('div'); const input = document.createElement('input');
    input.type = 'file'; host.append(input); document.body.append(host);
    let actions, latest, threw = false, policyLeaked = false;
    await mountCustom(host, '/uploads', 'minimal', (snapshot, commands) => {
        actions = commands; latest = snapshot;
        if (!snapshot.features) return;
        if (snapshot.features.chunking) { policyLeaked = true; throw new Error('Snapshot mutation leaked into policy.'); }
        snapshot.features.chunking = true; // Copied state must not change transport settings.
        if (snapshot.jobs.length && !threw) { threw = true; throw new Error('Intentional presentation failure.'); }
    });
    actions.setPickerOpen(true);
    if (canReloadSafely()) throw new Error('Picker barrier was lost.');
    actions.setPickerOpen(false);
    await actions.addFiles([new File(['custom renderer'], 'custom.txt')]);
    const deadline = Date.now() + 15000;
    while (latest.jobs[0]?.state !== 'complete') {
        if (Date.now() > deadline) throw new Error('Custom upload did not recover.');
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    if (!threw || policyLeaked || host.firstChild !== input || latest.jobs[0].offset !== 15)
        throw new Error('Native input or transfer state was lost.');
    await actions.dismiss(latest.jobs[0].id);
    if (latest.jobs.length) throw new Error('Dismiss did not reconcile.');
    host.remove();
    return { passed: true, nativeInputPreserved: true, renderFailureIsolated: true, snapshotPolicyIsolated: true };
})()
