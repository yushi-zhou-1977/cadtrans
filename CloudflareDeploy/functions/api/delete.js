const JWT_SECRET = 'c6c8c5abcae0442ebc2a2231e47c7d555ec2443af38e40689546e227f31b1531';

function verifyToken(token) {
    try {
        const payload = JSON.parse(atob(token));
        return payload.exp > Math.floor(Date.now() / 1000);
    } catch {
        return false;
    }
}

function jsonResponse(obj, status = 200) {
    return new Response(JSON.stringify(obj), {
        status,
        headers: { 'Content-Type': 'application/json' }
    });
}

function authenticate(request) {
    const token = request.headers.get('Authorization');
    if (!token) return false;
    return verifyToken(token.replace('Bearer ', ''));
}

async function getLicensesData(env) {
    try {
        const data = await env.LICENSES_KV.get('cadtrans_licenses');
        return data ? JSON.parse(data) : {};
    } catch {
        return {};
    }
}

async function saveLicensesData(env, licenses) {
    await env.LICENSES_KV.put('cadtrans_licenses', JSON.stringify(licenses));
}

export async function onRequestPost(context) {
    const { request, env } = context;

    if (!authenticate(request)) {
        return jsonResponse({ success: false, message: '未授权' }, 401);
    }

    if (!env.LICENSES_KV) {
        return jsonResponse({ success: false, message: 'KV命名空间未绑定，请在Cloudflare设置中绑定LICENSES_KV' });
    }

    try {
        const body = await request.json();
        const licenses = await getLicensesData(env);

        const key = (body.key || '').toUpperCase();
        if (!key) {
            return jsonResponse({ success: false, message: '请提供密钥' });
        }
        if (!licenses[key]) {
            return jsonResponse({ success: false, message: '密钥不存在' });
        }

        delete licenses[key];
        await saveLicensesData(env, licenses);
        return jsonResponse({ success: true });
    } catch (err) {
        return jsonResponse({ success: false, message: '请求格式错误: ' + err.message });
    }
}

export async function onRequestDelete(context) {
    return onRequestPost(context);
}
