// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

// Antes de excluir uma coleção, o modal repete o que vai acontecer com os vídeos
// conforme o rádio marcado. A delegação fica no documento para a navegação aprimorada.
(function () {
    document.addEventListener('show.bs.modal', function (evento) {
        const modal = evento.target;
        if (!modal || !modal.querySelector) {
            return;
        }

        const texto = modal.querySelector('[data-confirmacao-texto]');
        const formulario = modal.closest('form');
        if (!texto || !formulario) {
            return;
        }

        const escolhido = formulario.querySelector('input[name="videos"]:checked')
            || formulario.querySelector('input[name="videos"]');
        const valor = escolhido ? escolhido.value : '';
        const select = formulario.querySelector('select[name="destino"]');
        const semDestino = valor === 'mover' && !(select && select.value);
        const modelo = modal.querySelector('[data-confirmacao="' + (semDestino ? 'mover-vazio' : valor) + '"]');
        const confirmar = modal.querySelector('button[type="submit"]');

        if (confirmar) {
            confirmar.disabled = semDestino;
        }

        if (!modelo) {
            return;
        }

        const nome = select && select.selectedOptions.length > 0
            ? select.selectedOptions[0].textContent.trim()
            : '';

        texto.textContent = modelo.textContent.trim().replaceAll('{0}', nome);
    });
}());
