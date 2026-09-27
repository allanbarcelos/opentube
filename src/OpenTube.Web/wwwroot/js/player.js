// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

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

    function pedirTelaCheia(elemento) {
        const pedir = elemento.requestFullscreen || elemento.webkitRequestFullscreen;
        if (!pedir) {
            return;
        }

        try {
            const pedido = pedir.call(elemento);
            if (pedido && pedido.catch) {
                pedido.catch(function () { });
            }
        } catch (_) {
            // Sem gesto do usuário o navegador recusa; o vídeo só continua fora da tela cheia.
        }
    }

    function sairDaTelaCheia() {
        const sair = document.exitFullscreen || document.webkitExitFullscreen;
        return Promise.resolve(sair ? sair.call(document) : undefined).catch(function () { });
    }

    // Proteção básica contra o usuário comum. Não impede gravação nem print de tela — nada
    // no navegador impede —, mas tira os atalhos de cópia (menu de salvar, arrastar o vídeo)
    // e oferece a tela cheia do contêiner, com a marca d'água por cima.
    function proteger(video) {
        const shell = video.closest('.player-shell');
        if (!shell || shell.dataset.protegido) {
            return;
        }

        shell.dataset.protegido = '1';

        shell.addEventListener('contextmenu', function (e) { e.preventDefault(); });
        shell.addEventListener('dragstart', function (e) { e.preventDefault(); });

        // Na página do vídeo, controles próprios (controles.js) no lugar dos nativos, com os
        // capítulos na barra de progresso e a tela cheia do contêiner no botão deles.
        if (window.openTubeControles && window.openTubeControles.montar(video)) {
            video.addEventListener('dblclick', function (e) {
                e.preventDefault();
                const botao = shell.querySelector('.ctl-tela-cheia');
                if (botao) {
                    botao.click();
                }
            });
        } else if (shell.requestFullscreen || shell.webkitRequestFullscreen) {
            const botao = document.createElement('button');
            botao.type = 'button';
            botao.className = 'player-tela-cheia';
            botao.textContent = '⛶';
            botao.title = shell.dataset.fullscreenLabel || 'Full screen';
            botao.setAttribute('aria-label', botao.title);
            botao.addEventListener('click', function () {
                if (document.fullscreenElement || document.webkitFullscreenElement) {
                    sairDaTelaCheia();
                } else {
                    pedirTelaCheia(shell);
                }
            });
            shell.appendChild(botao);

            // Duplo clique, como nos players comuns, mas no contêiner.
            video.addEventListener('dblclick', function (e) {
                e.preventDefault();
                botao.click();
            });
        }

        // Tela cheia nativa (controle do Safari e do Firefox, iPhone) e Picture-in-Picture
        // ficam liberados. Nesses modos o navegador desenha só o vídeo, sem o que está por
        // cima dele: a marca d'água passa a ir como legenda, que o navegador desenha junto.
        if (window.openTubeMarcaDagua) {
            window.openTubeMarcaDagua.acompanharModosNativos(video);
        }
    }

    // Instância da hls.js que toca o vídeo (para o menu de qualidade), se houver uma.
    function hlsDe(video) {
        let achada = null;
        instancias.forEach(function (instancia) {
            if (instancia.video === video) {
                achada = instancia.hls;
            }
        });
        return achada;
    }

    // O vídeo da página, se houver um montado.
    function videoDaPagina() {
        return document.querySelector('video[data-manifest][data-montado]');
    }

    // Leva o player a um instante e toca de lá, com o player à vista. Usado pelos tempos
    // citados nas mensagens e pelo sumário do vídeo.
    function irPara(segundos) {
        const video = videoDaPagina();
        if (!video || !isFinite(segundos)) {
            return false;
        }

        const aplicar = function () {
            video.currentTime = Math.max(0, segundos);
            const tocando = video.play();
            if (tocando && tocando.catch) {
                tocando.catch(function () { /* o navegador pode exigir um clique para tocar */ });
            }
        };

        if (video.readyState >= 1) {
            aplicar();
        } else {
            video.addEventListener('loadedmetadata', aplicar, { once: true });
        }

        const caixa = video.getBoundingClientRect();
        if (caixa.top < 0 || caixa.bottom > window.innerHeight) {
            video.scrollIntoView({ behavior: 'smooth', block: 'center' });
        }
        return true;
    }

    // "?t=65" no endereço abre o vídeo naquele instante, como no YouTube.
    function aplicarInicioDoEndereco(video) {
        const t = parseInt(new URLSearchParams(window.location.search).get('t'), 10);
        if (!isNaN(t) && t > 0) {
            video.addEventListener('loadedmetadata', function () {
                video.currentTime = t;
            }, { once: true });
        }
    }

    // Tempo citado numa mensagem: na página do vídeo, em vez de navegar, leva o player ao
    // instante. Na captura, antes da navegação aprimorada do Blazor tratar o clique.
    document.addEventListener('click', function (evento) {
        const link = evento.target.closest && evento.target.closest('a[data-instante]');
        if (!link || link.target === '_blank' || evento.ctrlKey || evento.metaKey || evento.shiftKey) {
            return;
        }

        if (irPara(parseInt(link.dataset.instante, 10))) {
            evento.preventDefault();
            evento.stopImmediatePropagation();
        }
    }, true);

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
            proteger(video);
            aplicarInicioDoEndereco(video);

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

    return { iniciar: iniciar, encerrar: encerrar, montar: montar, irPara: irPara, hlsDe: hlsDe };
})();
