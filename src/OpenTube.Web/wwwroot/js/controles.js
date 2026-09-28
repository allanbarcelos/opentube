// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

// Controles próprios do player da página do vídeo, no lugar dos nativos.
//
// Os controles nativos não aceitam marcas na barra de progresso: aqui a barra é dividida nos
// capítulos do vídeo, como no YouTube — um pedaço por capítulo, separados por um vão, o
// pedaço sob o mouse engrossa e a dica mostra o título e o instante. O resto acompanha o que
// os players costumam ter: tocar e pausar, volume, tempo, legendas, velocidade, qualidade,
// Picture-in-Picture e tela cheia (do contêiner, para a marca d'água continuar por cima).
//
// Só vale para o contêiner marcado com data-controles; sem JavaScript o <video> fica com os
// controles nativos que vêm no HTML. Chamado pelo player.js ao montar cada vídeo.
window.openTubeControles = (function () {
    const OCULTAR_APOS = 2500;
    const VELOCIDADES = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2];
    const VOLUME = 'opentube.volume';

    // Ícones do bootstrap-icons, os mesmos do resto da aplicação.
    const ICONES = {
        tocar: 'play-fill',
        pausar: 'pause-fill',
        som: 'volume-up-fill',
        somBaixo: 'volume-down-fill',
        mudo: 'volume-mute-fill',
        cheia: 'fullscreen',
        sairDaCheia: 'fullscreen-exit',
        pip: 'pip',
        ajustes: 'gear-fill',
        legendas: 'badge-cc',
        legendasLigadas: 'badge-cc-fill'
    };

    function icone(b, nome) {
        if (b.dataset.icone === nome) {
            return;
        }
        b.dataset.icone = nome;
        b.innerHTML = '<i class="bi bi-' + ICONES[nome] + '" aria-hidden="true"></i>';
    }

    function formatar(segundos) {
        const s = Math.max(0, Math.floor(segundos || 0));
        const h = Math.floor(s / 3600);
        const m = Math.floor((s % 3600) / 60);
        const r = String(s % 60).padStart(2, '0');
        return h > 0 ? h + ':' + String(m).padStart(2, '0') + ':' + r : m + ':' + r;
    }

    function lerJson(texto, padrao) {
        try {
            return texto ? JSON.parse(texto) : padrao;
        } catch (_) {
            return padrao;
        }
    }

    function elemento(tag, classe, pai) {
        const el = document.createElement(tag);
        if (classe) {
            el.className = classe;
        }
        if (pai) {
            pai.appendChild(el);
        }
        return el;
    }

    function botao(classe, rotulo, pai) {
        const b = elemento('button', 'ctl-botao ' + classe, pai);
        b.type = 'button';
        rotular(b, rotulo);
        return b;
    }

    function rotular(b, rotulo) {
        b.title = rotulo;
        b.setAttribute('aria-label', rotulo);
    }

    // Trilhas do menu de legendas: as dos <track> da página. Ficam fora a da marca d'água
    // (marca-dagua.js) e as que o Safari cria sozinho a partir do HLS, sem nome.
    function legendasDe(video) {
        return Array.from(video.querySelectorAll('track')).map(function (t) { return t.track; })
            .filter(function (t) { return t && !t.openTubeMarca; });
    }

    function montar(video) {
        const shell = video.closest('.player-shell[data-controles]');
        if (!shell || shell.dataset.controlesMontados) {
            return false;
        }
        shell.dataset.controlesMontados = '1';

        const r = lerJson(shell.dataset.rotulos, {});
        const rotulo = function (chave, padrao) { return r[chave] || padrao; };

        // Capítulos: [{ inicio, fim, titulo }], já em ordem e cobrindo o vídeo inteiro.
        let capitulos = lerJson(shell.dataset.capitulos, []);

        video.controls = false;
        video.removeAttribute('controls');
        shell.classList.add('com-controles', 'ctl-visivel');

        // --- Estrutura -------------------------------------------------------------------
        const grande = botao('ctl-grande', rotulo('play', 'Play'), shell);
        icone(grande, 'tocar');

        const carregando = elemento('div', 'ctl-carregando', shell);
        carregando.setAttribute('aria-hidden', 'true');

        const barra = elemento('div', 'ctl', shell);

        const progresso = elemento('div', 'ctl-progresso', barra);
        progresso.tabIndex = 0;
        progresso.setAttribute('role', 'slider');
        progresso.setAttribute('aria-label', rotulo('progress', 'Video progress'));
        progresso.setAttribute('aria-valuemin', '0');

        const trilhos = elemento('div', 'ctl-trilhos', progresso);
        const bolinha = elemento('div', 'ctl-bolinha', progresso);
        const dica = elemento('div', 'ctl-dica', progresso);
        const dicaTitulo = elemento('span', 'ctl-dica-titulo', dica);
        const dicaTempo = elemento('span', 'ctl-dica-tempo', dica);

        const linha = elemento('div', 'ctl-linha', barra);
        const esquerda = elemento('div', 'ctl-grupo', linha);
        const direita = elemento('div', 'ctl-grupo', linha);

        const tocar = botao('ctl-tocar', rotulo('play', 'Play'), esquerda);
        const volumeGrupo = elemento('div', 'ctl-volume', esquerda);
        const mudo = botao('ctl-mudo', rotulo('mute', 'Mute'), volumeGrupo);
        const volume = elemento('input', 'ctl-volume-faixa', volumeGrupo);
        volume.type = 'range';
        volume.min = '0';
        volume.max = '1';
        volume.step = '0.05';
        volume.setAttribute('aria-label', rotulo('volume', 'Volume'));

        const tempo = elemento('span', 'ctl-tempo', esquerda);
        const capituloAtual = botao('ctl-capitulo', rotulo('chapters', 'Chapters'), esquerda);
        capituloAtual.hidden = true;

        const legendas = botao('ctl-legendas', rotulo('captions', 'Captions'), direita);
        icone(legendas, 'legendas');
        const ajustes = botao('ctl-ajustes', rotulo('settings', 'Settings'), direita);
        icone(ajustes, 'ajustes');

        const podePip = document.pictureInPictureEnabled && typeof video.requestPictureInPicture === 'function'
            || typeof video.webkitSetPresentationMode === 'function';
        const pip = podePip ? botao('ctl-pip', rotulo('pip', 'Picture in picture'), direita) : null;
        if (pip) {
            icone(pip, 'pip');
        }

        const telaCheiaDoShell = shell.requestFullscreen || shell.webkitRequestFullscreen;
        const telaCheiaDoVideo = typeof video.webkitEnterFullscreen === 'function';
        const telaCheia = telaCheiaDoShell || telaCheiaDoVideo
            ? botao('ctl-tela-cheia', rotulo('fullscreen', 'Full screen'), direita)
            : null;

        const menu = elemento('div', 'ctl-menu', shell);
        menu.hidden = true;
        menu.setAttribute('role', 'menu');

        // --- Capítulos na barra ----------------------------------------------------------
        let pedacos = [];

        function duracao() {
            return isFinite(video.duration) && video.duration > 0
                ? video.duration
                : (capitulos.length ? capitulos[capitulos.length - 1].fim : 0);
        }

        function desenharTrilhos() {
            trilhos.textContent = '';
            const total = duracao();
            const partes = capitulos.length > 0 ? capitulos : [{ inicio: 0, fim: total || 1, titulo: '' }];

            // O último capítulo termina onde o vídeo termina de fato, mesmo que a duração
            // cadastrada seja arredondada.
            if (capitulos.length > 0 && total > 0) {
                partes[partes.length - 1].fim = Math.max(partes[partes.length - 1].inicio + 0.1, total);
            }

            pedacos = partes.map(function (c) {
                const pedaco = elemento('div', 'ctl-pedaco', trilhos);
                pedaco.style.flexGrow = String(Math.max(0.1, c.fim - c.inicio));
                return {
                    el: pedaco,
                    carregado: elemento('div', 'ctl-carregado', pedaco),
                    apontado: elemento('div', 'ctl-apontado', pedaco),
                    tocado: elemento('div', 'ctl-tocado', pedaco),
                    inicio: c.inicio,
                    fim: c.fim,
                    titulo: c.titulo || ''
                };
            });
            progresso.classList.toggle('com-capitulos', capitulos.length > 1);
        }

        function capituloEm(t) {
            let achado = null;
            pedacos.forEach(function (p) {
                if (t >= p.inicio) {
                    achado = p;
                }
            });
            return achado;
        }

        function encher(chave, ate) {
            pedacos.forEach(function (p) {
                const parte = Math.min(1, Math.max(0, (ate - p.inicio) / Math.max(0.001, p.fim - p.inicio)));
                p[chave].style.transform = 'scaleX(' + parte + ')';
            });
        }

        // Instante sob o ponteiro. O vão entre os pedaços pertence ao que vem antes.
        function instanteEm(clientX) {
            const caixa = trilhos.getBoundingClientRect();
            const x = Math.min(caixa.right, Math.max(caixa.left, clientX));
            for (let i = 0; i < pedacos.length; i++) {
                const c = pedacos[i].el.getBoundingClientRect();
                const seguinte = i + 1 < pedacos.length ? pedacos[i + 1].el.getBoundingClientRect().left : caixa.right + 1;
                if (x < seguinte) {
                    const fracao = Math.min(1, Math.max(0, (x - c.left) / Math.max(1, c.width)));
                    return pedacos[i].inicio + fracao * (pedacos[i].fim - pedacos[i].inicio);
                }
            }
            return duracao();
        }

        // Posição horizontal de um instante, em pixels a partir do início da barra.
        function posicaoDe(t) {
            const caixa = trilhos.getBoundingClientRect();
            const p = capituloEm(t) || pedacos[0];
            if (!p) {
                return 0;
            }
            const c = p.el.getBoundingClientRect();
            const fracao = Math.min(1, Math.max(0, (t - p.inicio) / Math.max(0.001, p.fim - p.inicio)));
            return c.left - caixa.left + fracao * c.width;
        }

        // --- Estado ----------------------------------------------------------------------
        let arrastando = false;
        let ultimoCapitulo = null;

        function atualizarTempo() {
            const t = arrastando ? arrastando.t : video.currentTime;
            const total = duracao();

            encher('tocado', t);
            bolinha.style.left = posicaoDe(t) + 'px';
            tempo.textContent = formatar(t) + ' / ' + formatar(total);

            progresso.setAttribute('aria-valuemax', String(Math.floor(total)));
            progresso.setAttribute('aria-valuenow', String(Math.floor(t)));
            const atual = capitulos.length > 0 ? capituloEm(t) : null;
            progresso.setAttribute('aria-valuetext', formatar(t) + (atual && atual.titulo ? ' · ' + atual.titulo : ''));

            if (atual !== ultimoCapitulo) {
                ultimoCapitulo = atual;
                pedacos.forEach(function (p) { p.el.classList.toggle('atual', p === atual); });
                capituloAtual.hidden = !(atual && atual.titulo);
                capituloAtual.textContent = '';
                if (atual && atual.titulo) {
                    const titulo = elemento('span', 'ctl-capitulo-titulo', capituloAtual);
                    titulo.textContent = atual.titulo;
                    elemento('i', 'bi bi-chevron-right', capituloAtual).setAttribute('aria-hidden', 'true');
                }
            }
        }

        function atualizarCarregado() {
            const t = video.currentTime;
            let fim = 0;
            for (let i = 0; i < video.buffered.length; i++) {
                if (video.buffered.start(i) <= t + 0.5 && video.buffered.end(i) > fim) {
                    fim = video.buffered.end(i);
                }
            }
            encher('carregado', fim);
        }

        function atualizarTocar() {
            const pausado = video.paused || video.ended;
            const r1 = pausado ? rotulo('play', 'Play') : rotulo('pause', 'Pause');
            rotular(tocar, r1);
            icone(tocar, pausado ? 'tocar' : 'pausar');
            shell.classList.toggle('ctl-pausado', pausado);
            grande.hidden = !pausado || arrastando !== false;
            if (pausado) {
                mostrar();
            } else {
                agendarOcultar();
            }
        }

        function atualizarVolume() {
            const silencioso = video.muted || video.volume === 0;
            volume.value = silencioso ? '0' : String(video.volume);
            icone(mudo, silencioso ? 'mudo' : video.volume < 0.5 ? 'somBaixo' : 'som');
            rotular(mudo, silencioso ? rotulo('unmute', 'Unmute') : rotulo('mute', 'Mute'));
            volume.style.setProperty('--nivel', (silencioso ? 0 : video.volume * 100) + '%');
        }

        function atualizarLegendas() {
            const lista = legendasDe(video);
            legendas.hidden = lista.length === 0;
            const ligada = lista.some(function (t) { return t.mode === 'showing'; });
            legendas.classList.toggle('ligado', ligada);
            icone(legendas, ligada ? 'legendasLigadas' : 'legendas');
        }

        function atualizarTelaCheia() {
            if (!telaCheia) {
                return;
            }
            const cheia = (document.fullscreenElement || document.webkitFullscreenElement) === shell;
            icone(telaCheia, cheia ? 'sairDaCheia' : 'cheia');
            rotular(telaCheia, cheia ? rotulo('exitFullscreen', 'Exit full screen') : rotulo('fullscreen', 'Full screen'));
        }

        // --- Mostrar e esconder ----------------------------------------------------------
        let temporizador = null;

        function mostrar() {
            shell.classList.add('ctl-visivel');
            agendarOcultar();
        }

        function agendarOcultar() {
            clearTimeout(temporizador);
            temporizador = setTimeout(function () {
                const focoPeloTeclado = shell.dataset.porTeclado === '1' && barra.contains(document.activeElement);
                if (video.paused || arrastando || !menu.hidden || barra.matches(':hover') || focoPeloTeclado) {
                    return;
                }
                shell.classList.remove('ctl-visivel');
            }, OCULTAR_APOS);
        }

        shell.addEventListener('pointermove', mostrar);
        shell.addEventListener('pointerdown', mostrar);
        shell.addEventListener('focusin', mostrar);
        shell.addEventListener('pointerleave', function () {
            if (!video.paused && menu.hidden && !arrastando) {
                clearTimeout(temporizador);
                shell.classList.remove('ctl-visivel');
            }
        });

        // --- Ações -----------------------------------------------------------------------
        function alternar() {
            if (video.paused || video.ended) {
                const p = video.play();
                if (p && p.catch) {
                    p.catch(function () { });
                }
            } else {
                video.pause();
            }
        }

        function saltar(segundos) {
            video.currentTime = Math.min(duracao(), Math.max(0, video.currentTime + segundos));
            mostrar();
        }

        function alternarTelaCheia() {
            if (document.fullscreenElement || document.webkitFullscreenElement) {
                const sair = document.exitFullscreen || document.webkitExitFullscreen;
                Promise.resolve(sair && sair.call(document)).catch(function () { });
            } else if (telaCheiaDoShell) {
                Promise.resolve(telaCheiaDoShell.call(shell)).catch(function () { });
            } else if (telaCheiaDoVideo) {
                // iPhone: só o <video> vai para a tela cheia, com o player do sistema; a marca
                // d'água segue como legenda (marca-dagua.js).
                video.webkitEnterFullscreen();
            }
        }

        function alternarLegendas() {
            const lista = legendasDe(video);
            if (lista.length === 0) {
                return;
            }
            const ligada = lista.find(function (t) { return t.mode === 'showing'; });
            escolherLegenda(ligada ? null : lista[0]);
        }

        function escolherLegenda(trilha) {
            legendasDe(video).forEach(function (t) {
                t.mode = t === trilha ? 'showing' : 'disabled';
            });
            atualizarLegendas();
        }

        grande.addEventListener('click', alternar);
        tocar.addEventListener('click', alternar);
        // No toque, como no YouTube: com os controles escondidos, o primeiro toque só os mostra.
        let toqueComControlesEscondidos = false;
        shell.addEventListener('pointerdown', function (e) {
            toqueComControlesEscondidos = e.pointerType !== 'mouse' && !shell.classList.contains('ctl-visivel');
        }, true);
        video.addEventListener('click', function () {
            if (!menu.hidden) {
                fecharMenu();
                return;
            }
            if (toqueComControlesEscondidos) {
                return;
            }
            alternar();
        });
        mudo.addEventListener('click', function () {
            if (video.muted || video.volume === 0) {
                video.muted = false;
                if (video.volume === 0) {
                    video.volume = 0.5;
                }
            } else {
                video.muted = true;
            }
        });
        volume.addEventListener('input', function () {
            video.volume = parseFloat(volume.value);
            video.muted = video.volume === 0;
        });
        capituloAtual.addEventListener('click', function () {
            const lista = document.querySelector('[data-sumario-lista]');
            if (lista) {
                lista.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
                const ativo = lista.querySelector('[aria-current="true"]') || lista.querySelector('a');
                if (ativo) {
                    ativo.focus({ preventScroll: true });
                }
            }
        });
        if (telaCheia) {
            telaCheia.addEventListener('click', alternarTelaCheia);
        }
        if (pip) {
            pip.addEventListener('click', function () {
                if (document.pictureInPictureElement === video) {
                    document.exitPictureInPicture().catch(function () { });
                } else if (typeof video.requestPictureInPicture === 'function') {
                    video.requestPictureInPicture().catch(function () { });
                } else {
                    video.webkitSetPresentationMode(video.webkitPresentationMode === 'picture-in-picture'
                        ? 'inline' : 'picture-in-picture');
                }
            });
        }

        // --- Menus (legendas e ajustes) --------------------------------------------------
        let menuDe = null;

        function fecharMenu() {
            menu.hidden = true;
            menuDe = null;
            legendas.setAttribute('aria-expanded', 'false');
            ajustes.setAttribute('aria-expanded', 'false');
            agendarOcultar();
        }

        function opcao(texto, marcada, acao) {
            const b = elemento('button', 'ctl-opcao', menu);
            b.type = 'button';
            b.setAttribute('role', 'menuitemradio');
            b.setAttribute('aria-checked', marcada ? 'true' : 'false');
            const marca = elemento('i', 'bi bi-check2 ctl-opcao-marca', b);
            marca.setAttribute('aria-hidden', 'true');
            elemento('span', null, b).textContent = texto;
            b.addEventListener('click', function () {
                acao();
                fecharMenu();
            });
            return b;
        }

        function titulo(texto) {
            elemento('div', 'ctl-menu-titulo', menu).textContent = texto;
        }

        function abrirMenu(de) {
            if (menuDe === de) {
                fecharMenu();
                return;
            }
            menu.textContent = '';

            if (de === legendas) {
                titulo(rotulo('captions', 'Captions'));
                const lista = legendasDe(video);
                opcao(rotulo('off', 'Off'), !lista.some(function (t) { return t.mode === 'showing'; }),
                    function () { escolherLegenda(null); });
                lista.forEach(function (t) {
                    opcao(t.label || t.language, t.mode === 'showing', function () { escolherLegenda(t); });
                });
            } else {
                titulo(rotulo('speed', 'Playback speed'));
                VELOCIDADES.forEach(function (v) {
                    opcao(v === 1 ? rotulo('normal', 'Normal') : v.toLocaleString(document.documentElement.lang || undefined) + '×',
                        video.playbackRate === v, function () { video.playbackRate = v; });
                });

                const hls = window.openTubePlayer && window.openTubePlayer.hlsDe
                    ? window.openTubePlayer.hlsDe(video) : null;
                if (hls && hls.levels && hls.levels.length > 1) {
                    titulo(rotulo('quality', 'Quality'));
                    const atual = hls.autoLevelEnabled ? -1 : hls.currentLevel;
                    const nivelAuto = hls.levels[hls.currentLevel];
                    opcao(rotulo('auto', 'Auto') + (hls.autoLevelEnabled && nivelAuto ? ' (' + nivelAuto.height + 'p)' : ''),
                        atual === -1, function () { hls.currentLevel = -1; });
                    hls.levels
                        .map(function (n, i) { return { altura: n.height, indice: i }; })
                        .sort(function (a, b) { return b.altura - a.altura; })
                        .forEach(function (n) {
                            opcao(n.altura + 'p', atual === n.indice, function () { hls.currentLevel = n.indice; });
                        });
                }
            }

            menu.hidden = false;
            menuDe = de;
            de.setAttribute('aria-expanded', 'true');
            mostrar();
            const marcada = menu.querySelector('[aria-checked="true"]');
            if (marcada && shell.dataset.porTeclado === '1') {
                marcada.focus();
            }
        }

        legendas.setAttribute('aria-haspopup', 'menu');
        ajustes.setAttribute('aria-haspopup', 'menu');
        legendas.addEventListener('click', function () { abrirMenu(legendas); });
        ajustes.addEventListener('click', function () { abrirMenu(ajustes); });
        document.addEventListener('pointerdown', function (e) {
            if (!menu.hidden && !menu.contains(e.target) && e.target !== legendas && e.target !== ajustes
                && !legendas.contains(e.target) && !ajustes.contains(e.target)) {
                fecharMenu();
            }
        });

        // --- Barra de progresso ----------------------------------------------------------
        function apontar(clientX) {
            const t = instanteEm(clientX);
            const p = capituloEm(t);
            pedacos.forEach(function (x) { x.el.classList.toggle('apontado', x === p && capitulos.length > 1); });
            encher('apontado', t);

            dicaTitulo.textContent = p && p.titulo ? p.titulo : '';
            dicaTitulo.hidden = !dicaTitulo.textContent;
            dicaTempo.textContent = formatar(t);
            dica.hidden = false;

            // A dica segue o ponteiro sem sair do player.
            const caixa = progresso.getBoundingClientRect();
            const largura = dica.offsetWidth;
            const x = Math.min(caixa.width - largura / 2, Math.max(largura / 2, clientX - caixa.left));
            dica.style.left = x + 'px';
            return t;
        }

        function soltarApontador() {
            dica.hidden = true;
            pedacos.forEach(function (x) { x.el.classList.remove('apontado'); });
            encher('apontado', 0);
        }
        dica.hidden = true;

        progresso.addEventListener('pointermove', function (e) {
            const t = apontar(e.clientX);
            if (arrastando) {
                arrastando.t = t;
                video.currentTime = t;
                atualizarTempo();
            }
        });
        progresso.addEventListener('pointerleave', function () {
            if (!arrastando) {
                soltarApontador();
            }
        });
        progresso.addEventListener('pointerdown', function (e) {
            if (e.button !== 0) {
                return;
            }
            e.preventDefault();
            progresso.setPointerCapture(e.pointerId);
            const t = apontar(e.clientX);
            arrastando = { t: t, tocava: !video.paused };
            progresso.classList.add('arrastando');
            video.currentTime = t;
            atualizarTempo();
        });

        function terminarArrasto(e) {
            if (!arrastando) {
                return;
            }
            progresso.classList.remove('arrastando');
            arrastando = false;
            if (e && e.pointerType !== 'mouse') {
                soltarApontador();
            }
            atualizarTempo();
            atualizarTocar();
        }
        progresso.addEventListener('pointerup', terminarArrasto);
        progresso.addEventListener('pointercancel', terminarArrasto);

        progresso.addEventListener('keydown', function (e) {
            const passos = { ArrowLeft: -5, ArrowRight: 5, PageDown: -10, PageUp: 10 };
            if (e.key in passos) {
                e.preventDefault();
                e.stopPropagation();
                saltar(passos[e.key]);
            } else if (e.key === 'Home' || e.key === 'End') {
                e.preventDefault();
                video.currentTime = e.key === 'Home' ? 0 : duracao();
            }
        });

        // --- Teclado ---------------------------------------------------------------------
        // Atalhos do YouTube. Espaço e setas só quando o foco está no player, para não tomar a
        // rolagem da página; as letras valem na página toda, fora de campos de texto.
        document.addEventListener('keydown', function (e) {
            if (!shell.isConnected || e.ctrlKey || e.metaKey || e.altKey) {
                return;
            }
            const alvo = e.target;
            if (alvo && (alvo.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(alvo.tagName) && alvo !== volume)) {
                return;
            }
            const noPlayer = shell.contains(alvo);
            const noBotao = alvo && alvo.tagName === 'BUTTON';
            const tecla = e.key.length === 1 ? e.key.toLowerCase() : e.key;
            let tratou = true;

            if (tecla === 'Escape' && !menu.hidden) {
                fecharMenu();
                ajustes.focus();
            } else if (tecla === 'k' || (tecla === ' ' && noPlayer && !noBotao)) {
                alternar();
            } else if (tecla === 'j') {
                saltar(-10);
            } else if (tecla === 'l') {
                saltar(10);
            } else if (tecla === 'm') {
                mudo.click();
            } else if (tecla === 'f') {
                alternarTelaCheia();
            } else if (tecla === 'c') {
                alternarLegendas();
            } else if (noPlayer && alvo !== volume && (tecla === 'ArrowLeft' || tecla === 'ArrowRight')) {
                saltar(tecla === 'ArrowLeft' ? -5 : 5);
            } else if (noPlayer && alvo !== volume && (tecla === 'ArrowUp' || tecla === 'ArrowDown')) {
                video.muted = false;
                video.volume = Math.min(1, Math.max(0, video.volume + (tecla === 'ArrowUp' ? 0.05 : -0.05)));
            } else {
                tratou = false;
            }

            if (tratou) {
                e.preventDefault();
                shell.dataset.porTeclado = '1';
                mostrar();
            }
        });
        shell.addEventListener('pointerdown', function () { shell.dataset.porTeclado = ''; });

        // --- Eventos do vídeo ------------------------------------------------------------
        ['timeupdate', 'seeking', 'seeked'].forEach(function (nome) {
            video.addEventListener(nome, function () {
                atualizarTempo();
                atualizarCarregado();
            });
        });
        video.addEventListener('progress', atualizarCarregado);
        ['play', 'pause', 'ended'].forEach(function (nome) { video.addEventListener(nome, atualizarTocar); });
        video.addEventListener('volumechange', function () {
            atualizarVolume();
            try {
                localStorage.setItem(VOLUME, JSON.stringify({ volume: video.volume, mudo: video.muted }));
            } catch (_) { /* sem armazenamento, o volume só não é lembrado */ }
        });
        video.addEventListener('loadedmetadata', function () {
            desenharTrilhos();
            ultimoCapitulo = null;
            atualizarTempo();
        });
        video.addEventListener('waiting', function () { shell.classList.add('ctl-esperando'); });
        ['playing', 'canplay', 'pause'].forEach(function (nome) {
            video.addEventListener(nome, function () { shell.classList.remove('ctl-esperando'); });
        });
        if (video.textTracks) {
            video.textTracks.addEventListener('change', atualizarLegendas);
            video.textTracks.addEventListener('addtrack', atualizarLegendas);
        }
        document.addEventListener('fullscreenchange', atualizarTelaCheia);
        document.addEventListener('webkitfullscreenchange', atualizarTelaCheia);
        window.addEventListener('resize', atualizarTempo);

        try {
            const salvo = JSON.parse(localStorage.getItem(VOLUME) || 'null');
            if (salvo && typeof salvo.volume === 'number') {
                video.volume = Math.min(1, Math.max(0, salvo.volume));
                video.muted = !!salvo.mudo;
            }
        } catch (_) { /* segue com o volume padrão */ }

        desenharTrilhos();
        atualizarTempo();
        atualizarTocar();
        atualizarVolume();
        atualizarLegendas();
        atualizarTelaCheia();
        fecharMenu();
        return true;
    }

    return { montar: montar, formatar: formatar };
})();
