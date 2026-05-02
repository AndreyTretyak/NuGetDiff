const DB_NAME = 'nugetdiff';
const STORE = 'packages';
const DB_VERSION = 1;
const MAX_CACHE_BYTES = 500 * 1024 * 1024; // soft 500 MB cap

let dbPromise = null;

function openDb() {
    if (dbPromise) return dbPromise;
    dbPromise = new Promise((resolve, reject) => {
        const req = indexedDB.open(DB_NAME, DB_VERSION);
        req.onupgradeneeded = () => {
            const db = req.result;
            if (!db.objectStoreNames.contains(STORE)) {
                const store = db.createObjectStore(STORE, { keyPath: 'key' });
                store.createIndex('lastAccess', 'lastAccess');
            }
        };
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error);
    });
    return dbPromise;
}

export async function getPackage(key) {
    const db = await openDb();
    return new Promise((resolve, reject) => {
        const tx = db.transaction(STORE, 'readwrite');
        const store = tx.objectStore(STORE);
        const getReq = store.get(key);
        getReq.onsuccess = () => {
            const row = getReq.result;
            if (!row) {
                resolve(null);
                return;
            }
            row.lastAccess = Date.now();
            store.put(row);
            resolve(new Uint8Array(row.bytes));
        };
        getReq.onerror = () => reject(getReq.error);
    });
}

export async function putPackage(key, bytes) {
    const db = await openDb();
    const buffer = bytes instanceof Uint8Array ? bytes.buffer : bytes;
    await new Promise((resolve, reject) => {
        const tx = db.transaction(STORE, 'readwrite');
        const store = tx.objectStore(STORE);
        const req = store.put({ key, bytes: buffer, size: buffer.byteLength, lastAccess: Date.now() });
        req.onsuccess = () => resolve();
        req.onerror = () => reject(req.error);
    });
    await evictIfNeeded(db);
}

async function evictIfNeeded(db) {
    const total = await sumSize(db);
    if (total <= MAX_CACHE_BYTES) return;

    const candidates = await listByLastAccess(db);
    let freed = 0;
    const target = total - MAX_CACHE_BYTES;
    for (const row of candidates) {
        await deleteKey(db, row.key);
        freed += row.size;
        if (freed >= target) break;
    }
}

function sumSize(db) {
    return new Promise((resolve, reject) => {
        const tx = db.transaction(STORE, 'readonly');
        const store = tx.objectStore(STORE);
        const req = store.openCursor();
        let total = 0;
        req.onsuccess = e => {
            const cursor = e.target.result;
            if (cursor) {
                total += cursor.value.size || 0;
                cursor.continue();
            } else {
                resolve(total);
            }
        };
        req.onerror = () => reject(req.error);
    });
}

function listByLastAccess(db) {
    return new Promise((resolve, reject) => {
        const tx = db.transaction(STORE, 'readonly');
        const store = tx.objectStore(STORE);
        const idx = store.index('lastAccess');
        const out = [];
        const req = idx.openCursor();
        req.onsuccess = e => {
            const cursor = e.target.result;
            if (cursor) {
                out.push({ key: cursor.value.key, size: cursor.value.size || 0 });
                cursor.continue();
            } else {
                resolve(out);
            }
        };
        req.onerror = () => reject(req.error);
    });
}

function deleteKey(db, key) {
    return new Promise((resolve, reject) => {
        const tx = db.transaction(STORE, 'readwrite');
        const store = tx.objectStore(STORE);
        const req = store.delete(key);
        req.onsuccess = () => resolve();
        req.onerror = () => reject(req.error);
    });
}
