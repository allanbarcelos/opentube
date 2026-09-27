// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

// Leitura ponto a ponto da curva de retenção. Um gráfico desenhado numa página é
// interativo por natureza: sem o cursor, saber o valor de um instante exige contar
// pixels na tela.
(function () {
    function instante(segundos) {
        const total = Math.max(0, Math.round(segundos));
        const horas = Math.floor(total / 3600);
        const minutos = Math.floor((total % 3600) / 60);
        const restante = total % 60;

        const doisDigitos = function (n) { return n < 10 ? '0' + n : String(n); };

        return horas > 0
            ? horas + ':' + doisDigitos(minutos) + ':' + doisDigitos(restante)
            : minutos + ':' + doisDigitos(restante);
    }

    function ligar(svg) {
        const linha = svg.querySelector('.grafico-linha');
        const cursor = svg.querySelector('[data-cursor]');
        const marcador = svg.querySelector('[data-marcador]');
        const legenda = svg.parentElement.querySelector('[data-legenda-retencao]');

        if (!linha || !cursor || !marcador || !legenda) {
            return;
        }

        const textoOriginal = legenda.textContent;
        const duracao = parseFloat(svg.dataset.duracao || '0');
        const comprimento = linha.getTotalLength();

        function mover(evento) {
            const caixa = svg.getBoundingClientRect();
            const escala = svg.viewBox.baseVal.width / caixa.width;
            const x = (evento.clientX - caixa.left) * escala;

            // Percorre a própria linha em vez de recalcular a escala: assim o ponto
            // mostrado é exatamente o que está desenhado.
            let melhor = linha.getPointAtLength(0);
            let menorDistancia = Infinity;

            for (let passo = 0; passo <= 100; passo++) {
                const ponto = linha.getPointAtLength((comprimento * passo) / 100);
                const distancia = Math.abs(ponto.x - x);

                if (distancia < menorDistancia) {
                    menorDistancia = distancia;
                    melhor = ponto;
                }
            }

            cursor.setAttribute('x1', melhor.x);
            cursor.setAttribute('x2', melhor.x);
            cursor.classList.remove('d-none');

            marcador.setAttribute('cx', melhor.x);
            marcador.setAttribute('cy', melhor.y);
            marcador.classList.remove('d-none');

            const caixaDaLinha = linha.getBBox();
            const fracao = caixaDaLinha.width === 0 ? 0 : (melhor.x - caixaDaLinha.x) / caixaDaLinha.width;
            const percentual = Math.round(100 - ((melhor.y - 12) / (svg.viewBox.baseVal.height - 40)) * 100);

            const texto = duracao > 0
                ? (svg.dataset.comDuracao || '{0} — {1}% of the audience')
                : (svg.dataset.semDuracao || '{0}% of the video — {1}% of the audience');
            const primeiro = duracao > 0 ? instante(duracao * fracao) : String(Math.round(fracao * 100));

            legenda.textContent = texto.replace('{0}', primeiro).replace('{1}', String(Math.max(0, Math.min(100, percentual))));
        }

        function sair() {
            cursor.classList.add('d-none');
            marcador.classList.add('d-none');
            legenda.textContent = textoOriginal;
        }

        svg.addEventListener('pointermove', mover);
        svg.addEventListener('pointerleave', sair);
    }

    function iniciar() {
        document.querySelectorAll('[data-grafico-retencao]').forEach(ligar);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', iniciar);
    } else {
        iniciar();
    }
})();
