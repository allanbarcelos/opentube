// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

// Botão "Copiar": copia o valor do campo apontado em data-copiar e confirma no próprio botão.
// A delegação fica no documento, então vale também nas páginas da navegação aprimorada.
(function () {
    function copiar(campo) {
        if (navigator.clipboard && window.isSecureContext) {
            return navigator.clipboard.writeText(campo.value);
        }

        // Sem HTTPS (acesso pela rede local) a API da área de transferência não existe.
        campo.select();
        return document.execCommand('copy') ? Promise.resolve() : Promise.reject(new Error('cópia recusada'));
    }

    document.addEventListener('click', function (evento) {
        const botao = evento.target.closest && evento.target.closest('[data-copiar]');
        if (!botao) {
            return;
        }

        const campo = document.getElementById(botao.dataset.copiar);
        if (!campo) {
            return;
        }

        copiar(campo).then(function () {
            if (!botao.dataset.textoOriginal) {
                botao.dataset.textoOriginal = botao.textContent;
            }
            botao.textContent = botao.dataset.copiado || 'Copied';
            botao.classList.add('btn-success', 'copiado');
            botao.classList.remove('btn-outline-secondary');
            clearTimeout(botao._copiado);
            botao._copiado = setTimeout(function () {
                botao.textContent = botao.dataset.textoOriginal;
                botao.classList.remove('btn-success', 'copiado');
                botao.classList.add('btn-outline-secondary');
            }, 2000);
        }, function () {
            // Sem permissão: o texto fica selecionado para o Ctrl+C.
            campo.select();
        });
    });
})();
