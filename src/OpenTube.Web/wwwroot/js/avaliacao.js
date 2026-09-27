// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

// Avaliação de utilidade na página do vídeo. Envia a nota pelo fetch, sem recarregar a página
// (o vídeo continua tocando), e marca as estrelas até a escolhida. Sem script, o formulário
// comum faz o mesmo, recarregando.
//
// Carregado para todas as páginas no App.razor; a delegação no documento vale também para as
// páginas que chegam pela navegação aprimorada do Blazor.
(function () {
    function marcar(form, nota) {
        form.querySelectorAll('button[name="nota"]').forEach(function (botao) {
            const valor = parseInt(botao.value, 10);
            botao.classList.toggle('marcada', valor <= nota);
            botao.setAttribute('aria-pressed', valor === nota ? 'true' : 'false');
        });
    }

    document.addEventListener('submit', async function (evento) {
        const form = evento.target;
        if (!form.matches || !form.matches('[data-avaliacao-form]')) {
            return;
        }

        const botao = evento.submitter;
        if (!botao || botao.name !== 'nota') {
            return;
        }

        evento.preventDefault();

        const caixa = form.closest('[data-avaliacao]');
        const situacao = caixa.querySelector('[data-avaliacao-situacao]');
        const dados = new FormData(form);
        dados.set('nota', botao.value);

        try {
            const resposta = await fetch(form.action, {
                method: 'POST',
                headers: { 'Accept': 'application/json' },
                body: dados
            });
            const corpo = await resposta.json().catch(function () { return {}; });

            if (!resposta.ok) {
                throw new Error(corpo.erro || caixa.dataset.textoErro);
            }

            marcar(form, corpo.nota);
            situacao.textContent = caixa.dataset.textoMinha.replace('{0}', corpo.nota);
        } catch (falha) {
            situacao.textContent = '';
            const erro = document.createElement('span');
            erro.className = 'text-danger';
            erro.textContent = falha.message || caixa.dataset.textoErro;
            situacao.appendChild(erro);
        }
    });
})();
