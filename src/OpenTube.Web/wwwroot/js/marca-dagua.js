// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

// Identificação de quem assiste, sobre o vídeo. Não impede a gravação da tela — nada no
// navegador impede —, mas faz a gravação carregar quem estava vendo e quando.
//
// São duas camadas: um mosaico fraco, repetido sobre o quadro inteiro, que não sai num
// recorte; e uma etiqueta mais legível, com data e hora, que muda de canto de tempos em
// tempos, porque parada num lugar só seria coberta ou recortada sem esforço.
window.openTubeMarcaDagua = (function () {
    const ETIQUETA = 'marca-dagua';
    const MOSAICO = 'marca-dagua-mosaico';
    const INTERVALO = 12000;

    // Mesma ordem das posições da marca do acervo (WatermarkPosition), para poder pular a dela.
    const cantos = [
        { top: '8%', left: '6%', right: 'auto', bottom: 'auto' },
        { top: '8%', left: 'auto', right: '6%', bottom: 'auto' },
        { top: 'auto', left: '6%', right: 'auto', bottom: '14%' },
        { top: 'auto', left: 'auto', right: '6%', bottom: '14%' },
        { top: '42%', left: '30%', right: 'auto', bottom: 'auto' }
    ];

    function agora() {
        const idioma = document.documentElement.lang || undefined;

        try {
            return new Date().toLocaleString(idioma, { dateStyle: 'short', timeStyle: 'short' });
        } catch (_) {
            return new Date().toLocaleString();
        }
    }

    function escaparXml(texto) {
        return texto.replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    // O texto vai num SVG usado como fundo repetido: claro com contorno escuro, para aparecer
    // tanto em cena clara quanto escura.
    function desenharMosaico() {
        const mosaico = document.getElementById(MOSAICO);
        if (!mosaico || mosaico.dataset.desenhado) {
            return;
        }

        mosaico.dataset.desenhado = '1';

        const texto = escaparXml(mosaico.dataset.texto || '');
        const svg =
            '<svg xmlns="http://www.w3.org/2000/svg" width="360" height="200">' +
            '<text x="180" y="100" text-anchor="middle" dominant-baseline="middle" ' +
            'transform="rotate(-24 180 100)" font-family="sans-serif" font-size="15" ' +
            'fill="rgba(255,255,255,0.17)" stroke="rgba(0,0,0,0.17)" stroke-width="0.6">' +
            texto + '</text></svg>';

        mosaico.style.backgroundImage = 'url("data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg) + '")';
    }

    function iniciar(elementId) {
        const marca = document.getElementById(elementId);
        if (!marca || marca.dataset.iniciada) {
            return;
        }

        marca.dataset.iniciada = '1';

        const texto = marca.dataset.texto || marca.textContent;

        // A etiqueta não passa pelo lugar da imagem do acervo, onde ficaria por baixo dela.
        const shell = marca.closest('.player-shell');
        const ocupado = shell ? shell.dataset.logoCanto : undefined;
        const livres = cantos.filter(function (_, indice) { return String(indice) !== ocupado; });

        let posicao = Math.floor(Math.random() * livres.length);
        let temporizador = null;

        function mover() {
            // A navegação aprimorada tira a marca da página sem recarregar: o relógio para junto.
            if (!marca.isConnected) {
                clearInterval(temporizador);
                return;
            }

            const canto = livres[posicao % livres.length];

            marca.style.top = canto.top;
            marca.style.left = canto.left;
            marca.style.right = canto.right;
            marca.style.bottom = canto.bottom;
            marca.textContent = texto + ' · ' + agora();

            posicao++;
        }

        mover();
        temporizador = setInterval(mover, INTERVALO);
    }

    // Posições da legenda, em porcentagem do quadro (linha, coluna).
    const POSICOES_DA_LEGENDA = [
        { line: 8, position: 25 }, { line: 8, position: 75 },
        { line: 80, position: 25 }, { line: 80, position: 75 }, { line: 45, position: 50 }
    ];

    // Na tela cheia do próprio <video> (controle nativo do Safari e do Firefox, iPhone) e no
    // Picture-in-Picture, o navegador desenha só o vídeo: nada que esteja por cima aparece.
    // O que ele desenha junto são as legendas, então a marca vira uma legenda, visível só
    // enquanto o vídeo está num desses modos e reimposta se alguém a desligar no menu.
    // No Chromium a tela cheia nativa fica escondida e o PiP não desenha legendas: lá a
    // legenda não teria efeito e só apareceria no menu, por isso não é criada.
    function acompanharModosNativos(video) {
        const marca = document.getElementById(ETIQUETA);
        const chromium = video.controlsList && video.controlsList.supports
            && video.controlsList.supports('nofullscreen');

        if (!marca || chromium || video.dataset.marcaNativa
            || typeof video.addTextTrack !== 'function' || typeof VTTCue === 'undefined') {
            return;
        }

        video.dataset.marcaNativa = '1';

        const texto = marca.dataset.texto || marca.textContent;
        const trilha = video.addTextTrack('subtitles', texto, '');
        trilha.mode = 'hidden';

        let legenda = null;
        let posicao = Math.floor(Math.random() * POSICOES_DA_LEGENDA.length);

        function emModoNativo() {
            const telaCheia = document.fullscreenElement || document.webkitFullscreenElement;

            return telaCheia === video
                || video.webkitDisplayingFullscreen === true
                || (!!video.webkitPresentationMode && video.webkitPresentationMode !== 'inline')
                || document.pictureInPictureElement === video;
        }

        function ajustar() {
            const modo = emModoNativo() ? 'showing' : 'hidden';
            if (trilha.mode !== modo) {
                trilha.mode = modo;
            }
        }

        function mover() {
            if (legenda) {
                trilha.removeCue(legenda);
            }

            const lugar = POSICOES_DA_LEGENDA[posicao++ % POSICOES_DA_LEGENDA.length];

            legenda = new VTTCue(0, 360000, texto + ' · ' + agora());
            legenda.snapToLines = false;
            legenda.line = lugar.line;
            legenda.position = lugar.position;
            legenda.align = 'center';
            legenda.size = 60;
            trilha.addCue(legenda);
        }

        mover();

        const relogio = setInterval(function () {
            if (!video.isConnected) {
                clearInterval(relogio);
                return;
            }

            mover();
        }, INTERVALO);

        // Conferência frequente: cobre quem desliga a legenda no menu do player nativo.
        const vigia = setInterval(function () {
            if (!video.isConnected) {
                clearInterval(vigia);
                return;
            }

            ajustar();
        }, 1500);

        ['webkitbeginfullscreen', 'webkitendfullscreen', 'webkitpresentationmodechanged',
            'enterpictureinpicture', 'leavepictureinpicture'].forEach(function (evento) {
            video.addEventListener(evento, ajustar);
        });
        document.addEventListener('fullscreenchange', ajustar);
        document.addEventListener('webkitfullscreenchange', ajustar);
    }

    // Chamado pelo player a cada página montada.
    function montar() {
        desenharMosaico();
        iniciar(ETIQUETA);
    }

    return { iniciar: iniciar, montar: montar, acompanharModosNativos: acompanharModosNativos };
})();
