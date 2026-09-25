// Reprodução HLS. O Safari toca HLS nativamente; os demais navegadores precisam da hls.js,
// que é servida pelo próprio site para não depender de rede externa.
window.openTubePlayer = (function () {
    const instancias = new Map();

    function iniciar(elementId, manifestUrl) {
        const video = document.getElementById(elementId);
        if (!video) {
            return;
        }

        encerrar(elementId);

        if (video.canPlayType('application/vnd.apple.mpegurl')) {
            video.src = manifestUrl;
            return;
        }

        if (!window.Hls || !window.Hls.isSupported()) {
            video.insertAdjacentHTML(
                'afterend',
                '<div class="alert alert-warning mt-3">Este navegador não consegue reproduzir o vídeo.</div>');
            return;
        }

        const hls = new window.Hls({ enableWorker: true, lowLatencyMode: false });

        hls.on(window.Hls.Events.ERROR, function (_evento, dados) {
            if (!dados.fatal) {
                return;
            }

            // Erro de rede costuma ser assinatura vencida no meio da sessão: recarregar a
            // playlist traz endereços novos sem perder a posição.
            if (dados.type === window.Hls.ErrorTypes.NETWORK_ERROR) {
                hls.startLoad();
            } else if (dados.type === window.Hls.ErrorTypes.MEDIA_ERROR) {
                hls.recoverMediaError();
            } else {
                hls.destroy();
                instancias.delete(elementId);
            }
        });

        hls.loadSource(manifestUrl);
        hls.attachMedia(video);
        instancias.set(elementId, hls);
    }

    function encerrar(elementId) {
        const existente = instancias.get(elementId);
        if (existente) {
            existente.destroy();
            instancias.delete(elementId);
        }
    }

    return { iniciar: iniciar, encerrar: encerrar };
})();
