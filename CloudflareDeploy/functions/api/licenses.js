const JWT_SECRET = 'your_jwt_secret_here_must_be_long_and_random';

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

export async function onRequestGet(context) {
    const { request, env } = context;

    if (!authenticate(request)) {
        return jsonResponse({ success: false, message: '未授权' }, 401);
    }

    const url = new URL(request.url);
    const path = url.pathname;

    const licenses = await getLicensesData(env);

    if (path === '/api/licenses') {
        return jsonResponse({ success: true, licenses: Object.values(licenses) });
    }

    if (path === '/api/stats') {
        const total = Object.keys(licenses).length;
        const active = Object.values(licenses).filter(l => l.status === 'active' && new Date() <= new Date(l.expireDate)).length;
        const expired = Object.values(licenses).filter(l => l.status !== 'active' || new Date() > new Date(l.expireDate)).length;
        return jsonResponse({ success: true, total, active, expired });
    }

    return jsonResponse({ success: false, message: '未知操作' }, 404);
}
