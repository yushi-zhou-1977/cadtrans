const express = require('express');
const jwt = require('jsonwebtoken');
const cors = require('cors');
const path = require('path');
const fs = require('fs');

const app = express();
const PORT = 3000;

const ADMIN_USERNAME = 'admin';
const ADMIN_PASSWORD = 'your_admin_password_here';
const JWT_SECRET = 'your_jwt_secret_here_must_be_long_and_random';
const DB_FILE = './licenses.json';

let licenses = {};

function loadLicenses() {
    try {
        if (fs.existsSync(DB_FILE)) {
            const data = fs.readFileSync(DB_FILE, 'utf8');
            licenses = JSON.parse(data);
        }
    } catch (err) {
        console.error('加载许可证数据失败:', err.message);
        licenses = {};
    }
}

function saveLicenses() {
    try {
        fs.writeFileSync(DB_FILE, JSON.stringify(licenses, null, 2), 'utf8');
    } catch (err) {
        console.error('保存许可证数据失败:', err.message);
    }
}

loadLicenses();

app.use(cors());
app.use(express.json());
app.use(express.static(path.join(__dirname, '../Website')));

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
    return jwt.sign({ 
        exp: Math.floor(Date.now() / 1000) + 86400,
        iat: Math.floor(Date.now() / 1000)
    }, JWT_SECRET);
}

function verifyToken(token) {
    try {
        jwt.verify(token, JWT_SECRET);
        return true;
    } catch {
        return false;
    }
}

function authenticateToken(req, res, next) {
    const authHeader = req.headers['authorization'];
    const token = authHeader && authHeader.split(' ')[1];
    
    if (!token) {
        return res.status(401).json({ success: false, message: '未授权' });
    }
    
    if (!verifyToken(token)) {
        return res.status(401).json({ success: false, message: '令牌无效' });
    }
    
    next();
}

app.get('/api/validate', (req, res) => {
    const { key, fingerprint } = req.query;
    
    if (!key || !fingerprint) {
        return res.json({ valid: false, message: '参数不完整' });
    }
    
    const license = licenses[key.toUpperCase()];
    
    if (!license) {
        return res.json({ valid: false, message: '许可证密钥不存在' });
    }
    
    if (license.status !== 'active') {
        return res.json({ valid: false, message: '许可证已禁用' });
    }
    
    if (new Date() > new Date(license.expireDate)) {
        return res.json({ valid: false, message: '许可证已过期' });
    }
    
    if (license.fingerprint && license.fingerprint !== fingerprint) {
        return res.json({ valid: false, message: '机器指纹不匹配' });
    }
    
    if (!license.fingerprint) {
        license.fingerprint = fingerprint;
        saveLicenses();
    }
    
    res.json({
        valid: true,
        expireDate: license.expireDate,
        message: '授权有效'
    });
});

app.post('/api/login', (req, res) => {
    const { username, password } = req.body;
    
    if (username === ADMIN_USERNAME && password === ADMIN_PASSWORD) {
        const token = generateToken();
        res.json({ success: true, token });
    } else {
        res.json({ success: false, message: '用户名或密码错误' });
    }
});

app.post('/api/generate', authenticateToken, (req, res) => {
    const { days, note } = req.body;
    const daysInt = parseInt(days) || 365;
    
    const key = generateLicenseKey();
    const expireDate = new Date();
    expireDate.setDate(expireDate.getDate() + daysInt);
    const expireDateStr = expireDate.toISOString().split('T')[0];
    
    licenses[key] = {
        key,
        expireDate: expireDateStr,
        status: 'active',
        fingerprint: '',
        note: note || ''
    };
    
    saveLicenses();
    
    res.json({ success: true, key, expireDate: expireDateStr });
});

app.post('/api/disable', authenticateToken, (req, res) => {
    const { key } = req.body;
    
    if (licenses[key.toUpperCase()]) {
        licenses[key.toUpperCase()].status = 'disabled';
        saveLicenses();
        res.json({ success: true });
    } else {
        res.json({ success: false, message: '密钥不存在' });
    }
});

app.post('/api/reset', authenticateToken, (req, res) => {
    const { key } = req.body;
    
    if (licenses[key.toUpperCase()]) {
        licenses[key.toUpperCase()].fingerprint = '';
        saveLicenses();
        res.json({ success: true });
    } else {
        res.json({ success: false, message: '密钥不存在' });
    }
});

app.get('/api/licenses', authenticateToken, (req, res) => {
    const list = Object.values(licenses);
    res.json({ success: true, licenses: list });
});

app.get('/api/stats', authenticateToken, (req, res) => {
    const total = Object.keys(licenses).length;
    const active = Object.values(licenses).filter(l => l.status === 'active' && new Date() <= new Date(l.expireDate)).length;
    const expired = Object.values(licenses).filter(l => l.status !== 'active' || new Date() > new Date(l.expireDate)).length;
    
    res.json({ success: true, total, active, expired });
});

app.listen(PORT, () => {
    console.log(`\n========================================`);
    console.log(`CadTrans License Server 已启动`);
    console.log(`前端网站: http://localhost:${PORT}/`);
    console.log(`管理后台: http://localhost:${PORT}/admin.html`);
    console.log(`API地址: http://localhost:${PORT}/api`);
    console.log(`========================================\n`);
});
