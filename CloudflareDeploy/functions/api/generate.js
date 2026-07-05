const JWT_SECRET = 'c6c8c5abcae0442ebc2a2231e47c7d555ec2443af38e40689546e227f31b1531';

const LICENSE_TYPES = {
    daily_5k:   { days: 1,   dailyQuota: 5000,  label: '日卡-5K' },
    daily_50k:  { days: 1,   dailyQuota: 50000, label: '日卡-50K' },
    monthly_5k: { days: 30,  dailyQuota: 5000,  label: '月卡-5K' },
    monthly_50k:{ days: 30,  dailyQuota: 50000, label: '月卡-50K' },
    yearly_5k:  { days: 365, dailyQuota: 5000,  label: '年卡-5K' },
    yearly_50k: { days: 365, dailyQuota: 50000, label: '年卡-50K' }
};

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
        const path = new URL(request.url).pathname;
        const licenses = await getLicensesData(env);

        if (path === '/api/generate') {
            const type = body.type || '';
            const note = body.note || '';
            const fingerprint = body.fingerprint || '';
            const email = (body.email || '').trim();
            let days, dailyQuota;

            if (!fingerprint) {
                return jsonResponse({ success: false, message: '请输入机器指纹码' });
            }

            if (type && LICENSE_TYPES[type]) {
                // 新版：通过type查表获取days和dailyQuota
                days = LICENSE_TYPES[type].days;
                dailyQuota = LICENSE_TYPES[type].dailyQuota;
            } else {
                // 兼容旧版：直接传days参数
                days = parseInt(body.days) || 365;
                dailyQuota = 50000;
            }

            const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
            let key = '';
            for (let i = 0; i < 5; i++) {
                for (let j = 0; j < 4; j++) {
                    key += chars.charAt(Math.floor(Math.random() * chars.length));
                }
                if (i < 4) key += '-';
            }
            const expireDate = new Date();
            expireDate.setDate(expireDate.getDate() + days);
            licenses[key] = {
                key,
                expireDate: expireDate.toISOString().split('T')[0],
                status: 'active',
                fingerprint,
                email,
                note,
                type: type || 'legacy',
                dailyQuota,
                createdAt: new Date().toISOString()
            };
            await saveLicensesData(env, licenses);
            return jsonResponse({ success: true, key, expireDate: licenses[key].expireDate, type: type || 'legacy', dailyQuota });
        }

        if (path === '/api/disable') {
            if (licenses[body.key.toUpperCase()]) {
                licenses[body.key.toUpperCase()].status = 'disabled';
                await saveLicensesData(env, licenses);
                return jsonResponse({ success: true });
            }
            return jsonResponse({ success: false, message: '密钥不存在' });
        }

        if (path === '/api/reset') {
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
