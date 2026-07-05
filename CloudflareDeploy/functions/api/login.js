const ADMIN_USERNAME = 'admin';
const ADMIN_PASSWORD = '13621977041@iloveyou';
const JWT_SECRET = 'c6c8c5abcae0442ebc2a2231e47c7d555ec2443af38e40689546e227f31b1531';

function generateLicenseKey() {
    const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
    let key = '';
    for (let i = 0; i < 5; i++) {
        for (let j = 0; j < 4; j++) {
            key += chars.charAt(Math.floor(Math.random() * chars.length));
        }
        if (i < 4) key += '-';
    }
    return key;
}

function generateToken() {
    const payload = {
        exp: Math.floor(Date.now() / 1000) + 86400,
        iat: Math.floor(Date.now() / 1000)
    };
    return btoa(JSON.stringify(payload));
}

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

    try {
        const body = await request.json();
        const path = new URL(request.url).pathname;

        if (path === '/api/login') {
            if (body.username === ADMIN_USERNAME && body.password === ADMIN_PASSWORD) {
                return jsonResponse({ success: true, token: generateToken() });
            }
            return jsonResponse({ success: false, message: '用户名或密码错误' });
        }

        if (!authenticate(request)) {
            return jsonResponse({ success: false, message: '未授权' }, 401);
        }

        if (path === '/api/generate') {
            const days = parseInt(body.days) || 365;
            const note = body.note || '';
            const key = generateLicenseKey();
            const expireDate = new Date();
            expireDate.setDate(expireDate.getDate() + days);

            const licenses = await getLicensesData(env);
            licenses[key] = {
                key,
                expireDate: expireDate.toISOString().split('T')[0],
                status: 'active',
                fingerprint: '',
                note
            };
            await saveLicensesData(env, licenses);

            return jsonResponse({ success: true, key, expireDate: licenses[key].expireDate });
        }

        if (path === '/api/disable') {
            const licenses = await getLicensesData(env);
            if (licenses[body.key.toUpperCase()]) {
                licenses[body.key.toUpperCase()].status = 'disabled';
                await saveLicensesData(env, licenses);
                return jsonResponse({ success: true });
            }
            return jsonResponse({ success: false, message: '密钥不存在' });
        }

        if (path === '/api/reset') {
            const licenses = await getLicensesData(env);
            if (licenses[body.key.toUpperCase()]) {
                licenses[body.key.toUpperCase()].fingerprint = '';
                await saveLicensesData(env, licenses);
                return jsonResponse({ success: true });
            }
            return jsonResponse({ success: false, message: '密钥不存在' });
        }

        return jsonResponse({ success: false, message: '未知操作' }, 404);
    } catch {
        return jsonResponse({ success: false, message: '请求格式错误' });
    }
}
