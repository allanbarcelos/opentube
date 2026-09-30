// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

// Campo de etiquetas: cada vírgula, ponto e vírgula ou Enter vira um badge, com um X para tirar.
// O campo original continua no formulário, escondido, com as etiquetas separadas por vírgula:
// o servidor recebe o mesmo de sempre, e sem JavaScript o campo de texto comum segue valendo.
// As regras são as do servidor (Video.ReplaceTags): minúsculas, sem repetidas, até 30.
//
// Carregado para todas as páginas no App.razor; monta-se sozinho na carga inicial e a cada
// navegação aprimorada do Blazor.
window.openTubeEtiquetas = (function () {
    const SEPARADORES = /[,;\n]/;

    function montarUm(bloco) {
        if (bloco.dataset.montado === '1') {
            return;
        }
        const original = bloco.querySelector('input[data-etiquetas-valor]');
        if (!original) {
            return;
        }
        bloco.dataset.montado = '1';

        const limite = parseInt(bloco.dataset.limite, 10) || 30;
        const rotuloRemover = bloco.dataset.remover || 'Remove {0}';
        let etiquetas = [];

        // A caixa fica no lugar do campo; o rótulo passa a apontar para a digitação.
        const caixa = document.createElement('div');
        caixa.className = 'form-control caixa-etiquetas';
        const lista = document.createElement('div');
        lista.className = 'caixa-etiquetas-lista';
        lista.setAttribute('role', 'list');
        const digitacao = document.createElement('input');
        digitacao.type = 'text';
        digitacao.className = 'caixa-etiquetas-digitacao';
        digitacao.id = original.id;
        digitacao.autocomplete = 'off';
        digitacao.setAttribute('aria-describedby', original.getAttribute('aria-describedby') || '');
        original.id = original.id + '-valor';
        original.type = 'hidden';

        caixa.append(lista, digitacao);
        original.insertAdjacentElement('afterend', caixa);

        function normalizar(texto) {
            return texto.trim().toLowerCase();
        }

        function gravar() {
            original.value = etiquetas.join(', ');
            digitacao.placeholder = etiquetas.length === 0 ? (bloco.dataset.placeholder || '') : '';
            digitacao.disabled = etiquetas.length >= limite;
            caixa.classList.toggle('cheia', etiquetas.length >= limite);
        }

        function desenhar() {
            lista.replaceChildren();
            etiquetas.forEach(function (etiqueta) {
                const badge = document.createElement('span');
                badge.className = 'badge rounded-pill text-bg-light border etiqueta';
                badge.setAttribute('role', 'listitem');
                badge.dataset.etiqueta = etiqueta;

                const texto = document.createElement('span');
                texto.textContent = etiqueta;

                const remover = document.createElement('button');
                remover.type = 'button';
                remover.className = 'etiqueta-remover';
                const rotulo = rotuloRemover.replace('{0}', etiqueta);
                remover.title = rotulo;
                remover.setAttribute('aria-label', rotulo);
                remover.innerHTML = '<i class="bi bi-x" aria-hidden="true"></i>';
                remover.addEventListener('click', function (evento) {
                    evento.stopPropagation();
                    etiquetas = etiquetas.filter(function (e) { return e !== etiqueta; });
                    desenhar();
                    digitacao.focus();
                });

                badge.append(texto, remover);
                lista.append(badge);
            });
            gravar();
        }

        // Acrescenta o que houver no texto (pode ser uma lista colada) e devolve o que sobrou.
        function acrescentar(texto) {
            texto.split(SEPARADORES).forEach(function (parte) {
                const etiqueta = normalizar(parte);
                if (etiqueta && !etiquetas.includes(etiqueta) && etiquetas.length < limite) {
                    etiquetas.push(etiqueta);
                }
            });
            desenhar();
        }

        digitacao.addEventListener('input', function () {
            if (!SEPARADORES.test(digitacao.value)) {
                return;
            }
            // O último pedaço, depois do separador, continua sendo digitado.
            const partes = digitacao.value.split(SEPARADORES);
            const resto = partes.pop();
            acrescentar(partes.join(','));
            digitacao.value = resto.trimStart();
        });

        digitacao.addEventListener('keydown', function (evento) {
            if (evento.key === 'Enter') {
                // Enter confirma a etiqueta; não envia o formulário.
                evento.preventDefault();
                if (digitacao.value.trim()) {
                    acrescentar(digitacao.value);
                    digitacao.value = '';
                }
            } else if (evento.key === 'Backspace' && digitacao.value === '' && etiquetas.length > 0) {
                etiquetas.pop();
                desenhar();
            }
        });

        // Ao sair do campo, o texto que ficou vira etiqueta: nada digitado se perde ao salvar.
        digitacao.addEventListener('blur', function () {
            if (digitacao.value.trim()) {
                acrescentar(digitacao.value);
                digitacao.value = '';
            }
        });

        caixa.addEventListener('click', function (evento) {
            if (evento.target === caixa || evento.target === lista) {
                digitacao.focus();
            }
        });

        // O formulário pode ser enviado com texto ainda na digitação (Enter em outro campo).
        const formulario = original.form;
        if (formulario) {
            formulario.addEventListener('submit', function () {
                if (digitacao.value.trim()) {
                    acrescentar(digitacao.value);
                    digitacao.value = '';
                }
            });
        }

        acrescentar(original.value);
    }

    function montar() {
        document.querySelectorAll('[data-etiquetas]').forEach(montarUm);
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
