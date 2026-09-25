// Identificação de quem assiste, sobre o vídeo. Muda de canto de tempos em tempos: parada
// num lugar só, ela seria recortada da gravação sem esforço.
window.openTubeMarcaDagua = (function () {
    const INTERVALO = 20000;

    const cantos = [
        { top: '8%', left: '6%', right: 'auto', bottom: 'auto' },
        { top: '8%', left: 'auto', right: '6%', bottom: 'auto' },
        { top: 'auto', left: '6%', right: 'auto', bottom: '14%' },
        { top: 'auto', left: 'auto', right: '6%', bottom: '14%' }
    ];

    function iniciar(elementId) {
        const marca = document.getElementById(elementId);
        if (!marca) {
            return;
        }

        let posicao = Math.floor(Math.random() * cantos.length);

        function mover() {
            const canto = cantos[posicao % cantos.length];

            marca.style.top = canto.top;
            marca.style.left = canto.left;
            marca.style.right = canto.right;
            marca.style.bottom = canto.bottom;

            posicao++;
        }

        mover();
        setInterval(mover, INTERVALO);
    }

    return { iniciar: iniciar };
})();
