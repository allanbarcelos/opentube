// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

// Pequenos comportamentos de formulário que antes ficavam em atributos onclick/onfocus. A
// política de conteúdo não executa script escrito no próprio HTML, então eles vêm por atributos
// data-*, com a delegação no documento para valer também na navegação aprimorada.
(function () {
    // Pergunta antes de enviar: <form data-confirmar="Texto da pergunta">.
    document.addEventListener('submit', function (evento) {
        const formulario = evento.target;
        if (formulario.dataset && formulario.dataset.confirmar && !window.confirm(formulario.dataset.confirmar)) {
            evento.preventDefault();
        }
    }, true);

    // Ao entrar no campo, marca a opção a que ele pertence: <input data-marcar="id-do-radio">.
    document.addEventListener('focusin', function (evento) {
        const campo = evento.target;
        const alvo = campo.dataset && campo.dataset.marcar ? document.getElementById(campo.dataset.marcar) : null;
        if (alvo) {
            alvo.checked = true;
        }
    });

    // Seleciona o texto inteiro ao clicar, para copiar: <input data-selecionar>.
    document.addEventListener('click', function (evento) {
        const campo = evento.target;
        if (campo.matches && campo.matches('input[data-selecionar]')) {
            campo.select();
        }
    });

    // Envia o formulário assim que o valor muda: <input data-enviar-ao-mudar>.
    document.addEventListener('change', function (evento) {
        const campo = evento.target;
        if (campo.matches && campo.matches('[data-enviar-ao-mudar]') && campo.form) {
            campo.form.requestSubmit();
        }
    });
})();
