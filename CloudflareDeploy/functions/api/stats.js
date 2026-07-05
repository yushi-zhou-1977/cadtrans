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

export async function onRequestGet(context) {
    const { request, env } = context;

    if (!authenticate(request)) {
        return jsonResponse({ success: false, message: '未授权' }, 401);
    }

    if (!env.LICENSES_KV) {
        return jsonResponse({ success: false, message: 'KV命名空间未绑定，请在Cloudflare设置中绑定LICENSES_KV' });
    }

    try {
        const data = await env.LICENSES_KV.get('cadtrans_licenses');
        const licenses = data ? JSON.parse(data) : {};

        const total = Object.keys(licenses).length;
        const active = Object.values(licenses).filter(l => l.status === 'active' && new Date() <= new Date(l.expireDate)).length;
        const expired = Object.values(licenses).filter(l => l.status !== 'active' || new Date() > new Date(l.expireDate)).length;

        return jsonResponse({ success: true, total, active, expired });
    } catch (e) {
        return jsonResponse({ success: false, message: '查询失败: ' + e.message });
    }
}
