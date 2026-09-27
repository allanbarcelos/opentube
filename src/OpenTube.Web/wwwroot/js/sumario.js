// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

// Sumário do vídeo (capítulos).
//
// Na administração, o editor acrescenta e remove linhas de tempo e título.
// Na página do vídeo, a lista ao lado acompanha o vídeo, com o capítulo atual destacado;
// clicar leva o player ao ponto (pelo tratamento de [data-instante] do player.js). Os
// capítulos na barra de progresso ficam com os controles do player (controles.js).
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

    function montarLista(lista) {
        const video = document.getElementById('player-video');
        if (!video || lista.dataset.montado === '1') {
            return;
        }
        lista.dataset.montado = '1';

        const itens = Array.from(lista.querySelectorAll('[data-capitulo]'));
        let ultimo = null;

        // O capítulo atual é o último que já começou; antes do primeiro, nenhum.
        function atualizar() {
            const t = video.currentTime || 0;
            let atual = null;
            itens.forEach(function (item) {
                if (t >= parseFloat(item.dataset.instante)) {
                    atual = item;
                }
            });

            if (atual === ultimo) {
                return;
            }
            ultimo = atual;

            itens.forEach(function (item) {
                item.classList.toggle('active', item === atual);
                if (item === atual) {
                    item.setAttribute('aria-current', 'true');
                } else {
                    item.removeAttribute('aria-current');
                }
            });
        }

        // "seeking" dispara no instante do pulo: a lista acompanha o clique na hora, mesmo
        // quando o trecho ainda está sendo baixado e o "seeked" só vem depois.
        ['timeupdate', 'seeking', 'seeked', 'loadedmetadata'].forEach(function (nome) {
            video.addEventListener(nome, atualizar);
        });
        atualizar();
    }

    function montar() {
        document.querySelectorAll('[data-sumario-editor]').forEach(montarEditor);
        document.querySelectorAll('[data-sumario-lista]').forEach(montarLista);
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
