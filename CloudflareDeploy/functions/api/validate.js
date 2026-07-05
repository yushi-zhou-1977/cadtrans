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

export async function onRequestGet(context) {
    const { request, env } = context;
    const url = new URL(request.url);
    const key = url.searchParams.get('key');
    const fingerprint = url.searchParams.get('fingerprint');

    if (!key || !fingerprint) {
        return jsonResponse({ valid: false, message: '参数不完整' });
    }

    const licenses = await getLicensesData(env);
    const license = licenses[key.toUpperCase()];

    if (!license) {
        return jsonResponse({ valid: false, message: '许可证密钥不存在' });
    }

    if (license.status !== 'active') {
        return jsonResponse({ valid: false, message: '许可证已禁用' });
    }

    if (new Date() > new Date(license.expireDate)) {
        return jsonResponse({ valid: false, message: '许可证已过期' });
    }

    if (license.fingerprint && license.fingerprint !== fingerprint) {
        return jsonResponse({ valid: false, message: '机器指纹不匹配' });
    }

    if (!license.fingerprint) {
        license.fingerprint = fingerprint;
        await saveLicensesData(env, licenses);
    }

    // 旧记录迁移：缺少type/dailyQuota时自动补充
    if (!license.type) {
        license.type = 'legacy';
        license.dailyQuota = 50000;
        await saveLicensesData(env, licenses);
    }

    return jsonResponse({
        valid: true,
        expireDate: license.expireDate,
        message: '授权有效',
        dailyQuota: license.dailyQuota || 5000,
        type: license.type || 'unknown'
    });
}
