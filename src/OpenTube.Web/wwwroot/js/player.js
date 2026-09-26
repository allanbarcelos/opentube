// Reprodução HLS. O Safari toca HLS nativamente; os demais navegadores precisam da hls.js,
// que é servida pelo próprio site para não depender de rede externa.
//
// O script é carregado uma vez, no layout, e monta sozinho os players da página: a
// navegação aprimorada do Blazor troca o conteúdo sem executar os <script> da página nova,
// então nada que dependa de script inline chegaria a rodar ao abrir um vídeo pela home.
// A página só descreve o player, com atributos data- no <video>.
window.openTubePlayer = (function () {
    const TIPO_HLS = 'application/vnd.apple.mpegurl';
    const HLS_JS = 'lib/hlsjs/hls.min.js';
    const AVISO_PADRAO = 'This browser cannot play the video.';

    const instancias = new Map();
    let carregandoHls = null;

    // A hls.js tem meio megabyte: só é buscada quando há um vídeo na página, e uma vez só.
    function carregarHls() {
        if (window.Hls) {
            return Promise.resolve();
        }

        if (!carregandoHls) {
            carregandoHls = new Promise(function (resolve, reject) {
                const script = document.createElement('script');
                script.src = HLS_JS;
                script.onload = resolve;
                script.onerror = function () {
                    carregandoHls = null;
                    reject(new Error('Não foi possível carregar a hls.js.'));
                };
                document.head.appendChild(script);
            });
        }

        return carregandoHls;
    }

    function avisar(video) {
        // O texto vem traduzido no atributo data-unsupported e entra como texto, não HTML.
        const aviso = document.createElement('div');
        aviso.className = 'alert alert-warning mt-3';
        aviso.textContent = video.dataset.unsupported || AVISO_PADRAO;
        video.insertAdjacentElement('afterend', aviso);
    }

    function iniciar(elementId, manifestUrl, videoId) {
        const video = document.getElementById(elementId);
        if (!video) {
            return;
        }

        encerrar(elementId);

        const audiencia = videoId && window.openTubeAnalytics
            ? window.openTubeAnalytics.criar(videoId, video)
            : null;

        const instancia = { video: video, manifest: manifestUrl, hls: null, audiencia: audiencia };
        instancias.set(elementId, instancia);

        if (video.canPlayType(TIPO_HLS)) {
            video.src = manifestUrl;
            return;
        }

        if (!window.Hls || !window.Hls.isSupported()) {
            avisar(video);
            return;
        }

        const hls = new window.Hls({ enableWorker: true, lowLatencyMode: false });
        instancia.hls = hls;

        hls.on(window.Hls.Events.LEVEL_SWITCHED, function (_evento, dados) {
            const nivel = hls.levels[dados.level];

            if (audiencia && nivel) {
                audiencia.qualidade(nivel.height + 'p');
            }
        });

        hls.on(window.Hls.Events.ERROR, function (_evento, dados) {
            if (!dados.fatal) {
                return;
            }

            // Erro de rede costuma ser assinatura vencida no meio da sessão: recarregar a
            // playlist traz endereços novos sem perder a posição.
            if (audiencia) {
                audiencia.erro(dados.details || dados.type);
            }

            if (dados.type === window.Hls.ErrorTypes.NETWORK_ERROR) {
                hls.startLoad();
            } else if (dados.type === window.Hls.ErrorTypes.MEDIA_ERROR) {
                hls.recoverMediaError();
            } else {
                hls.destroy();
                instancia.hls = null;
            }
        });

        hls.loadSource(manifestUrl);
        hls.attachMedia(video);
    }

    function encerrar(elementId) {
        const existente = instancias.get(elementId);
        if (!existente) {
            return;
        }

        instancias.delete(elementId);

        if (existente.hls) {
            existente.hls.destroy();
        }

        if (existente.audiencia) {
            existente.audiencia.encerrar();
        }
    }

    // Monta os players descritos na página e encerra os que saíram dela. Roda na carga
    // inicial e a cada navegação aprimorada; montar duas vezes o mesmo vídeo não faz nada.
    function montar() {
        instancias.forEach(function (instancia, elementId) {
            if (!instancia.video.isConnected || instancia.video.dataset.manifest !== instancia.manifest) {
                encerrar(elementId);
            }
        });

        document.querySelectorAll('video[data-manifest]').forEach(function (video) {
            const manifest = video.dataset.manifest;

            if (!video.id || video.dataset.montado === manifest) {
                return;
            }

            video.dataset.montado = manifest;

            if (video.canPlayType(TIPO_HLS)) {
                iniciar(video.id, manifest, video.dataset.videoId);
                return;
            }

            carregarHls().then(
                function () { iniciar(video.id, manifest, video.dataset.videoId); },
                function () { avisar(video); });
        });

        if (window.openTubeMarcaDagua) {
            window.openTubeMarcaDagua.montar();
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', montar);
    } else {
        montar();
    }

    if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
        window.Blazor.addEventListener('enhancedload', montar);
    }

    return { iniciar: iniciar, encerrar: encerrar, montar: montar };
})();
