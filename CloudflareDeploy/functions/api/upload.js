const JWT_SECRET = 'c6c8c5abcae0442ebc2a2231e47c7d555ec2443af38e40689546e227f31b1531';

function verifyToken(token) {
    try {
        const payload = JSON.parse(atob(token));
        return payload.exp > Math.floor(Date.now() / 1000);
    } catch {
        return false;
    }
}

function authenticate(request) {
    const token = request.headers.get('Authorization');
    if (!token) return false;
    return verifyToken(token.replace('Bearer ', ''));
}

function jsonResponse(obj, status = 200) {
    return new Response(JSON.stringify(obj), {
        status,
        headers: { 'Content-Type': 'application/json' }
    });
}

export async function onRequestPost(context) {
    const { request, env } = context;

    if (!authenticate(request)) {
        return jsonResponse({ success: false, message: '未授权' }, 401);
    }

    if (!env.LICENSES_KV) {
        return jsonResponse({ success: false, message: 'KV命名空间未绑定' });
    }

    try {
        const formData = await request.formData();
        const file = formData.get('file');
        const version = formData.get('version') || '';
        const changelog = formData.get('changelog') || '';

        if (!file) {
            return jsonResponse({ success: false, message: '请选择文件' });
        }

        // Accept zip and dll files
        const filename = file.name.toLowerCase();
        if (!filename.endsWith('.zip') && !filename.endsWith('.dll')) {
            return jsonResponse({ success: false, message: '仅支持 .zip 或 .dll 文件' });
        }

        const arrayBuffer = await file.arrayBuffer();

        // Store the file binary
        await env.LICENSES_KV.put('cadtrans_file', arrayBuffer);

        // Store metadata
        const metadata = {
            filename: file.name,
            version: version,
            changelog: changelog,
            size: arrayBuffer.byteLength,
            uploadDate: new Date().toISOString(),
            contentType: file.type || 'application/octet-stream'
        };
        await env.LICENSES_KV.put('cadtrans_file_meta', JSON.stringify(metadata));

        return jsonResponse({
            success: true,
            message: '上传成功',
            metadata
        });
    } catch (err) {
        return jsonResponse({ success: false, message: '上传失败: ' + err.message });
    }
}

// GET: fetch metadata (public, no auth required for frontend display)
export async function onRequestGet(context) {
    const { env } = context;

    if (!env.LICENSES_KV) {
        return jsonResponse({ success: false, message: 'KV命名空间未绑定' });
    }

    try {
        const metaStr = await env.LICENSES_KV.get('cadtrans_file_meta');
        if (!metaStr) {
            return jsonResponse({ success: true, metadata: null });
        }
        const metadata = JSON.parse(metaStr);
        return jsonResponse({ success: true, metadata });
    } catch (err) {
        return jsonResponse({ success: false, message: '获取信息失败' });
    }
}

// DELETE: remove uploaded file (admin only)
export async function onRequestDelete(context) {
    const { request, env } = context;

    if (!authenticate(request)) {
        return jsonResponse({ success: false, message: '未授权' }, 401);
    }

    if (!env.LICENSES_KV) {
        return jsonResponse({ success: false, message: 'KV命名空间未绑定' });
    }

    try {
        await env.LICENSES_KV.delete('cadtrans_file');
        await env.LICENSES_KV.delete('cadtrans_file_meta');
        return jsonResponse({ success: true, message: '文件已删除' });
    } catch (err) {
        return jsonResponse({ success: false, message: '删除失败: ' + err.message });
    }
}
