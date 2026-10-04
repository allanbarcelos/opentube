// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

// Seletor com busca (SeletorDinamico.razor), no lugar de um <select> com a lista inteira do banco.
// As opções vêm do servidor vinte por vez, filtradas pelo que foi digitado: a busca espera a pessoa
// parar de digitar, e chegar ao fim da lista (rolando, com as setas ou pelo botão) pede a página
// seguinte. A opção escolhida vai para o campo escondido, que é o que o formulário envia.
//
// Teclado: setas percorrem as opções, Enter escolhe, Esc fecha. Segue o padrão de combobox do
// ARIA: o foco fica no campo, e a opção ativa é anunciada por aria-activedescendant.
//
// Carregado para todas as páginas no App.razor; monta-se sozinho na carga inicial e a cada
// navegação aprimorada do Blazor.
window.openTubeSeletor = (function () {
    const ESPERA = 250;

    function montarUm(bloco) {
        if (bloco.dataset.montado === '1') {
            return;
        }
        const busca = bloco.querySelector('[data-seletor-busca]');
        const valor = bloco.querySelector('[data-seletor-valor]');
        const lista = bloco.querySelector('[role="listbox"]');
        if (!busca || !valor || !lista) {
            return;
        }
        bloco.dataset.montado = '1';

        const textos = {
            vazio: bloco.dataset.vazio || 'Nothing found.',
            carregando: bloco.dataset.carregando || 'Loading…',
            mais: bloco.dataset.mais || 'Load more',
            erro: bloco.dataset.erro || 'Could not load the options.',
            escolha: bloco.dataset.escolha || 'Choose an option from the list.'
        };

        let termo = '';
        let pagina = 0;
        let haMais = false;
        let carregando = false;
        let ativa = -1;
        let espera = null;
        let pedido = null;
        let escolhido = '';

        function opcoes() {
            return Array.prototype.slice.call(lista.querySelectorAll('[role="option"]'));
        }

        function abrir() {
            lista.classList.add('show');
            busca.setAttribute('aria-expanded', 'true');
        }

        function fechar() {
            lista.classList.remove('show');
            busca.setAttribute('aria-expanded', 'false');
            busca.removeAttribute('aria-activedescendant');
            ativa = -1;
        }

        function aviso(texto, classe) {
            const item = document.createElement('li');
            item.className = 'dropdown-item-text small text-body-secondary ' + (classe || '');
            item.setAttribute('aria-live', 'polite');
            item.dataset.aviso = '1';
            item.textContent = texto;
            lista.appendChild(item);
        }

        function limparAvisos() {
            lista.querySelectorAll('[data-aviso], [data-mais]').forEach(function (e) { e.remove(); });
        }

        function marcar(indice) {
            const todas = opcoes();
            todas.forEach(function (o) { o.classList.remove('active'); o.setAttribute('aria-selected', 'false'); });
            ativa = indice;
            if (indice < 0 || indice >= todas.length) {
                busca.removeAttribute('aria-activedescendant');
                return;
            }
            const opcao = todas[indice];
            opcao.classList.add('active');
            opcao.setAttribute('aria-selected', 'true');
            busca.setAttribute('aria-activedescendant', opcao.id);
            opcao.scrollIntoView({ block: 'nearest' });

            // Chegar à última opção já pede as próximas.
            if (indice === todas.length - 1 && haMais) {
                carregar(false);
            }
        }

        function escolher(opcao) {
            valor.value = opcao.dataset.valor;
            escolhido = opcao.dataset.rotulo;
            busca.value = escolhido;
            busca.setCustomValidity('');
            fechar();
            valor.dispatchEvent(new Event('change', { bubbles: true }));
        }

        function acrescentar(itens) {
            const base = lista.querySelectorAll('[role="option"]').length;
            itens.forEach(function (item, i) {
                const opcao = document.createElement('li');
                opcao.id = busca.id + '-opcao-' + (base + i);
                opcao.className = 'dropdown-item seletor-opcao';
                opcao.setAttribute('role', 'option');
                opcao.setAttribute('aria-selected', 'false');
                opcao.dataset.valor = item.id;
                opcao.dataset.rotulo = item.label;

                const rotulo = document.createElement('span');
                rotulo.className = 'd-block text-truncate';
                rotulo.textContent = item.label;
                opcao.appendChild(rotulo);

                if (item.hint) {
                    const detalhe = document.createElement('small');
                    detalhe.className = 'd-block text-body-secondary text-truncate';
                    detalhe.textContent = item.hint;
                    opcao.appendChild(detalhe);
                }

                // mousedown, e não click: o click viria depois do blur do campo, que fecha a lista.
                opcao.addEventListener('mousedown', function (e) {
                    e.preventDefault();
                    escolher(opcao);
                });
                lista.appendChild(opcao);
            });
        }

        function botaoMais() {
            const item = document.createElement('li');
            item.dataset.mais = '1';
            const botao = document.createElement('button');
            botao.type = 'button';
            botao.className = 'dropdown-item text-primary small';
            botao.textContent = textos.mais;
            botao.addEventListener('mousedown', function (e) {
                e.preventDefault();
                carregar(false);
            });
            item.appendChild(botao);
            lista.appendChild(item);
        }

        // Do zero (busca nova) ou a página seguinte da mesma busca.
        function carregar(doZero) {
            if (carregando && !doZero) {
                return;
            }
            if (pedido) {
                pedido.abort();
            }
            if (doZero) {
                lista.innerHTML = '';
                pagina = 0;
                haMais = false;
                ativa = -1;
            }

            carregando = true;
            limparAvisos();
            aviso(textos.carregando);
            abrir();

            const controle = new AbortController();
            pedido = controle;
            const proxima = pagina + 1;
            const endereco = new URL(bloco.dataset.fonte, window.location.origin);
            if (termo) {
                endereco.searchParams.set('q', termo);
            }
            endereco.searchParams.set('page', String(proxima));

            fetch(endereco, { credentials: 'same-origin', cache: 'no-store', signal: controle.signal, headers: { Accept: 'application/json' } })
                .then(function (resposta) {
                    if (!resposta.ok) {
                        throw new Error(String(resposta.status));
                    }
                    return resposta.json();
                })
                .then(function (dados) {
                    if (pedido !== controle) {
                        return;
                    }
                    limparAvisos();
                    pagina = dados.page;
                    haMais = !!dados.hasMore;
                    acrescentar(dados.items || []);

                    if (opcoes().length === 0) {
                        aviso(textos.vazio);
                    } else if (haMais) {
                        botaoMais();
                    }
                })
                .catch(function (erro) {
                    if (erro && erro.name === 'AbortError') {
                        return;
                    }
                    limparAvisos();
                    aviso(textos.erro, 'text-danger');
                })
                .finally(function () {
                    if (pedido === controle) {
                        carregando = false;
                        pedido = null;
                    }
                });
        }

        busca.addEventListener('input', function () {
            // Digitar desfaz a escolha: o valor enviado é sempre o de uma opção da lista.
            valor.value = '';
            escolhido = '';
            busca.setCustomValidity('');
            termo = busca.value.trim();

            // As opções da busca anterior saem na hora: enquanto a nova não chega, um clique
            // escolheria uma opção que não corresponde mais ao que está escrito.
            if (pedido) {
                pedido.abort();
                pedido = null;
                carregando = false;
            }
            lista.innerHTML = '';
            pagina = 0;
            haMais = false;
            marcar(-1);
            aviso(textos.carregando);
            abrir();

            clearTimeout(espera);
            espera = setTimeout(function () { carregar(true); }, ESPERA);
        });

        busca.addEventListener('focus', function () {
            if (pagina === 0) {
                carregar(true);
            } else {
                abrir();
            }
        });

        busca.addEventListener('blur', function () {
            fechar();
            // Saiu sem escolher: o texto volta a ser o da opção escolhida, ou nada.
            if (busca.value !== escolhido) {
                busca.value = escolhido;
            }
        });

        busca.addEventListener('keydown', function (e) {
            const todas = opcoes();
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                if (!lista.classList.contains('show')) {
                    abrir();
                }
                marcar(Math.min(ativa + 1, todas.length - 1));
            } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                marcar(Math.max(ativa - 1, 0));
            } else if (e.key === 'Enter') {
                // Enter com a lista aberta escolhe; não envia o formulário pela metade.
                if (lista.classList.contains('show') && ativa >= 0 && todas[ativa]) {
                    e.preventDefault();
                    escolher(todas[ativa]);
                }
            } else if (e.key === 'Escape') {
                fechar();
            }
        });

        // Rolar até perto do fim pede a página seguinte.
        lista.addEventListener('scroll', function () {
            if (haMais && !carregando && lista.scrollTop + lista.clientHeight >= lista.scrollHeight - 24) {
                carregar(false);
            }
        });

        // Campo obrigatório: sem uma opção escolhida, o formulário não sai.
        const formulario = bloco.closest('form');
        if (formulario && bloco.dataset.obrigatorio === '1') {
            formulario.addEventListener('submit', function (e) {
                if (!valor.value) {
                    e.preventDefault();
                    e.stopImmediatePropagation();
                    busca.setCustomValidity(textos.escolha);
                    busca.reportValidity();
                }
            }, true);
        }
    }

    function montar() {
        document.querySelectorAll('[data-seletor]').forEach(montarUm);
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
