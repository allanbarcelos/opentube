// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

// Sumário do vídeo (capítulos).
//
// Na administração, o editor acrescenta e remove linhas de tempo e título.
// Na página do vídeo, a barra de capítulos sob o player e a lista ao lado acompanham o vídeo:
// o capítulo atual fica destacado, o segmento dele se enche conforme avança, e clicar leva o
// player ao ponto (na lista, pelo tratamento de [data-instante] do player.js).
//
// Carregado para todas as páginas no App.razor; monta-se sozinho na carga inicial e a cada
// navegação aprimorada do Blazor.
window.openTubeSumario = (function () {
    function montarEditor(editor) {
        if (editor.dataset.montado === '1') {
            return;
        }
        editor.dataset.montado = '1';

        const linhas = editor.querySelector('[data-sumario-linhas]');
        const modelo = editor.querySelector('template[data-sumario-modelo]');

        editor.addEventListener('click', function (evento) {
            const remover = evento.target.closest('[data-sumario-remover]');
            if (remover) {
                remover.closest('[data-sumario-linha]').remove();
                return;
            }

            if (evento.target.closest('[data-sumario-acrescentar]')) {
                const linha = modelo.content.firstElementChild.cloneNode(true);
                linhas.appendChild(linha);
                linha.querySelector('input[name="inicio"]').focus();
            }
        });
    }

    function montarPlayer(barra) {
        const video = document.getElementById('player-video');
        if (!video || barra.dataset.montado === '1') {
            return;
        }
        barra.dataset.montado = '1';

        const segmentos = Array.from(barra.querySelectorAll('.sumario-segmento')).map(function (botao) {
            return {
                botao: botao,
                preenchido: botao.querySelector('.sumario-preenchido'),
                inicio: parseFloat(botao.dataset.inicio),
                fim: parseFloat(botao.dataset.fim),
                titulo: botao.dataset.titulo || ''
            };
        });
        const itens = Array.from(document.querySelectorAll('[data-sumario-lista] [data-capitulo]'));
        const atual = document.querySelector('[data-sumario-atual]');
        let ultimo = -1;

        function atualizar() {
            const t = video.currentTime || 0;
            let indice = -1;

            segmentos.forEach(function (s, i) {
                const parte = Math.min(1, Math.max(0, (t - s.inicio) / Math.max(1, s.fim - s.inicio)));
                s.preenchido.style.width = (parte * 100) + '%';
                if (t >= s.inicio) {
                    indice = i;
                }
            });

            if (indice === ultimo) {
                return;
            }
            ultimo = indice;

            segmentos.forEach(function (s, i) {
                s.botao.classList.toggle('atual', i === indice);
            });

            // A lista tem só os capítulos com título (sem o trecho antes do primeiro): cada item
            // se reconhece pelo instante de início.
            const titulo = indice >= 0 ? segmentos[indice].titulo : '';
            const inicio = indice >= 0 ? segmentos[indice].inicio : -1;
            itens.forEach(function (item) {
                const ativo = titulo !== '' && parseFloat(item.dataset.instante) === inicio;
                item.classList.toggle('active', ativo);
                if (ativo) {
                    item.setAttribute('aria-current', 'true');
                } else {
                    item.removeAttribute('aria-current');
                }
            });

            if (atual) {
                atual.textContent = titulo ? '▸ ' + titulo : '';
            }
        }

        // Clicar num segmento leva ao ponto clicado dentro dele, como na barra do YouTube;
        // pelo teclado (sem posição), vai ao início do capítulo.
        barra.addEventListener('click', function (evento) {
            const botao = evento.target.closest('.sumario-segmento');
            if (!botao) {
                return;
            }
            const s = segmentos.find(function (x) { return x.botao === botao; });
            let destino = s.inicio;

            if (evento.detail > 0) {
                const caixa = botao.getBoundingClientRect();
                const fracao = Math.min(1, Math.max(0, (evento.clientX - caixa.left) / caixa.width));
                destino = s.inicio + fracao * (s.fim - s.inicio);
            }

            if (window.openTubePlayer && window.openTubePlayer.irPara) {
                window.openTubePlayer.irPara(destino);
            }
        });

        // "seeking" dispara no instante do pulo: a barra acompanha o clique na hora, mesmo quando
        // o trecho ainda está sendo baixado e o "seeked" só vem depois.
        ['timeupdate', 'seeking', 'seeked', 'loadedmetadata'].forEach(function (nome) {
            video.addEventListener(nome, atualizar);
        });
        atualizar();
    }

    function montar() {
        document.querySelectorAll('[data-sumario-editor]').forEach(montarEditor);
        document.querySelectorAll('[data-sumario-barra]').forEach(montarPlayer);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', montar);
    } else {
        montar();
    }

    if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
        window.Blazor.addEventListener('enhancedload', montar);
    }

    return { montar: montar };
})();
