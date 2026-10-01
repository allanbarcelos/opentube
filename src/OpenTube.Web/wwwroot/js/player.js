// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

// Reprodução HLS pela hls.js, servida pelo próprio site para não depender de rede externa. Ela
// monta o vídeo na memória (o <video> fica com um src blob:), inclusive no Safari e no iPhone que
// a suportam; o player nativo fica só para quem não tem como usá-la.
//
// A página não traz endereço de vídeo: só o id e um token (data-video, data-reproducao). O
// player troca o token pelo endereço opaco da reprodução, e daí em diante cada playlist e cada
// pedaço é pedido por um selo cifrado.
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

    // Troca o token da página pelo endereço da reprodução.
    function pedirReproducao(video) {
        return fetch('/api/play', {
            method: 'POST',
            credentials: 'same-origin',
            cache: 'no-store',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ video: video.dataset.video, token: video.dataset.reproducao })
        }).then(function (resposta) {
            if (!resposta.ok) {
                throw new Error('Reprodução recusada: ' + resposta.status);
            }

            return resposta.json();
        }).then(function (dados) { return dados.src; });
    }

    function iniciar(elementId, videoId) {
        const video = document.getElementById(elementId);
        if (!video) {
            return;
        }

        encerrar(elementId);

        const audiencia = videoId && window.openTubeAnalytics
            ? window.openTubeAnalytics.criar(videoId, video)
            : null;

        const instancia = { video: video, reproducao: video.dataset.reproducao, hls: null, audiencia: audiencia };
        instancias.set(elementId, instancia);

        pedirReproducao(video).then(function (src) {
            // A página pode ter trocado de vídeo enquanto o pedido estava no ar.
            if (instancias.get(elementId) !== instancia) {
                return;
            }

            if (window.Hls && window.Hls.isSupported()) {
                tocarComHls(instancia, src);
            } else if (video.canPlayType(TIPO_HLS)) {
                video.src = src;
            } else {
                avisar(video);
            }
        }, function () {
            if (instancias.get(elementId) === instancia) {
                avisar(video);
            }
        });
    }

    function tocarComHls(instancia, src) {
        const video = instancia.video;
        const audiencia = instancia.audiencia;

        // O servidor limita a velocidade com que os pedaços do vídeo saem, para que baixá-lo
        // leve quase tanto quanto assisti-lo. O player fica bem abaixo disso: adianta no máximo
        // um minuto, e quando o servidor pede calma (429) espera e tenta de novo, em vez de
        // tratar o pedido como erro de vez.
        const hls = new window.Hls({
            enableWorker: true,
            lowLatencyMode: false,
            // Com ele, o servidor responde as playlists com um tipo genérico, que não diz o que são.
            xhrSetup: function (xhr) {
                xhr.setRequestHeader('X-OpenTube-Player', 'hls');
            },
            maxBufferLength: 30,
            maxMaxBufferLength: 60,
            fragLoadPolicy: {
                default: {
                    maxTimeToFirstByteMs: 10000,
                    maxLoadTimeMs: 120000,
                    timeoutRetry: { maxNumRetry: 4, retryDelayMs: 0, maxRetryDelayMs: 0 },
                    errorRetry: {
                        maxNumRetry: 8,
                        retryDelayMs: 1000,
                        maxRetryDelayMs: 8000,
                        shouldRetry: function (politica, tentativa, _expirou, resposta, padrao) {
                            return padrao || (!!resposta && resposta.code === 429 && tentativa < politica.maxNumRetry);
                        }
                    }
                }
            }
        });
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

        hls.loadSource(src);
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
            botao.innerHTML = '<i class="bi bi-arrows-fullscreen" aria-hidden="true"></i>';
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

        // O Picture-in-Picture comum desenha só o vídeo, sem nada do que está por cima — a
        // marca d'água inclusive. Fica bloqueado: o atributo disablepictureinpicture tira a
        // opção dos navegadores, e se algum entrar mesmo assim, o vídeo sai do modo e pausa.
        // O PiP que leva a marca junto é o dos controles (controles.js).
        const recusarPip = function () {
            video.pause();
            if (document.pictureInPictureElement === video && document.exitPictureInPicture) {
                document.exitPictureInPicture().catch(function () { });
            }
        };
        video.disablePictureInPicture = true;
        video.addEventListener('enterpictureinpicture', recusarPip);
        video.addEventListener('webkitpresentationmodechanged', function () {
            if (video.webkitPresentationMode === 'picture-in-picture') {
                video.pause();
                video.webkitSetPresentationMode('inline');
            }
        });

        // A tela cheia nativa (controle do Safari e do Firefox, iPhone) fica liberada. Nela o
        // navegador desenha só o vídeo: a marca d'água passa a ir como legenda, que ele
        // desenha junto.
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
        return document.querySelector('video[data-reproducao][data-montado]');
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

    // O interruptor da playlist, no estilo do YouTube. Ausente no armazenamento significa ligado.
    function preferenciaAutoplay() {
        try {
            return localStorage.getItem('opentube.autoplay') !== '0';
        } catch (_) {
            return true;
        }
    }

    function guardarAutoplay(ligado) {
        try {
            localStorage.setItem('opentube.autoplay', ligado ? '1' : '0');
        } catch (_) {
            // Sessão privada pode recusar o armazenamento; o interruptor vale só nesta página.
        }
    }

    function prepararAutoplay() {
        document.querySelectorAll('[data-autoplay]').forEach(function (interruptor) {
            if (interruptor.dataset.ligado === '1') {
                return;
            }

            interruptor.dataset.ligado = '1';
            interruptor.checked = preferenciaAutoplay();
            interruptor.addEventListener('change', function () {
                guardarAutoplay(interruptor.checked);
            });
        });

        document.querySelectorAll('video[data-proximo]').forEach(function (video) {
            if (!video.dataset.proximo || video.dataset.autoplayPronto === '1') {
                return;
            }

            video.dataset.autoplayPronto = '1';
            video.addEventListener('ended', function () {
                if (preferenciaAutoplay() && video.dataset.proximo) {
                    window.location.assign(video.dataset.proximo);
                }
            });
        });
    }

    // "?autoplay=1" pede para o vídeo seguinte começar sozinho. O navegador pode recusar
    // sem um gesto; os controles ficam, e a pessoa carrega no play.
    function reproduzirSePedido(video) {
        if (new URLSearchParams(window.location.search).get('autoplay') !== '1') {
            return;
        }

        if (video.dataset.autoplayPedido === '1') {
            return;
        }

        video.dataset.autoplayPedido = '1';

        const tocar = function () {
            const pedido = video.play();
            if (pedido && pedido.catch) {
                pedido.catch(function () { });
            }
        };

        if (video.readyState >= 3) {
            tocar();
        } else {
            video.addEventListener('canplay', tocar, { once: true });
        }
    }

    // Monta os players descritos na página e encerra os que saíram dela. Roda na carga
    // inicial e a cada navegação aprimorada; montar duas vezes o mesmo vídeo não faz nada.
    function montar() {
        instancias.forEach(function (instancia, elementId) {
            if (!instancia.video.isConnected || instancia.video.dataset.reproducao !== instancia.reproducao) {
                encerrar(elementId);
            }
        });

        document.querySelectorAll('video[data-reproducao]').forEach(function (video) {
            reproduzirSePedido(video);

            const reproducao = video.dataset.reproducao;

            if (!video.id || video.dataset.montado === reproducao) {
                return;
            }

            video.dataset.montado = reproducao;
            proteger(video);
            aplicarInicioDoEndereco(video);

            // Sem a hls.js (falha ao carregá-la), ainda resta o player nativo onde houver.
            const comecar = function () { iniciar(video.id, video.dataset.videoId); };
            carregarHls().then(comecar, comecar);
        });

        if (window.openTubeMarcaDagua) {
            window.openTubeMarcaDagua.montar();
        }

        prepararAutoplay();
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
