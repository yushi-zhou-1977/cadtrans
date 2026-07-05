export async function onRequestGet(context) {
    const { env } = context;

    if (!env.LICENSES_KV) {
        return new Response('KV not bound', { status: 500 });
    }

    try {
        const data = await env.LICENSES_KV.get('cadtrans_dll', { type: 'arrayBuffer' });
        if (!data) {
            return new Response('File not found', { status: 404 });
        }

        const metaStr = await env.LICENSES_KV.get('cadtrans_dll_meta');
        let filename = 'CadTrans.dll';
        if (metaStr) {
            try {
                const meta = JSON.parse(metaStr);
                if (meta.filename) filename = meta.filename;
            } catch {}
        }

        return new Response(data, {
            status: 200,
            headers: {
                'Content-Type': 'application/octet-stream',
                'Content-Disposition': `attachment; filename="${filename}"`,
                'Content-Length': data.byteLength.toString(),
                'Cache-Control': 'no-cache'
            }
        });
    } catch (err) {
        return new Response('Download error', { status: 500 });
    }
}
