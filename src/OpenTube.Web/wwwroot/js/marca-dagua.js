// Identificação de quem assiste, sobre o vídeo. Muda de canto de tempos em tempos: parada
// num lugar só, ela seria recortada da gravação sem esforço.
window.openTubeMarcaDagua = (function () {
    const ID = 'marca-dagua';
    const INTERVALO = 20000;

    const cantos = [
        { top: '8%', left: '6%', right: 'auto', bottom: 'auto' },
        { top: '8%', left: 'auto', right: '6%', bottom: 'auto' },
        { top: 'auto', left: '6%', right: 'auto', bottom: '14%' },
        { top: 'auto', left: 'auto', right: '6%', bottom: '14%' }
    ];

    function iniciar(elementId) {
        const marca = document.getElementById(elementId);
        if (!marca || marca.dataset.iniciada) {
            return;
        }

        marca.dataset.iniciada = '1';

        let posicao = Math.floor(Math.random() * cantos.length);
        let temporizador = null;

        function mover() {
            // A navegação aprimorada tira a marca da página sem recarregar: o relógio para junto.
            if (!marca.isConnected) {
                clearInterval(temporizador);
                return;
            }

            const canto = cantos[posicao % cantos.length];

            marca.style.top = canto.top;
            marca.style.left = canto.left;
            marca.style.right = canto.right;
            marca.style.bottom = canto.bottom;

            posicao++;
        }

        mover();
        temporizador = setInterval(mover, INTERVALO);
    }

    // Chamado pelo player a cada página montada.
    function montar() {
        iniciar(ID);
    }

    return { iniciar: iniciar, montar: montar };
})();
