// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

// Editor de legendas no estilo de um editor de código: a primeira coluna numera as linhas, a
// segunda tem o tempo em que a legenda entra (e sai) e a terceira, o texto. Ao lado, o vídeo:
// a linha em exibição fica destacada e o texto aparece sobre ele enquanto se digita.
//
// Carregado no layout e montado sozinho, como o player: a navegação do Blazor não executa
// scripts da página nova.
window.openTubeEditorDeLegendas = (function () {
    const DURACAO_PADRAO = 2;

    // ── Tempo ────────────────────────────────────────────────────────────────
    function lerTempo(texto) {
        const achado = /^(?:(\d{1,3}):)?(\d{1,2}):(\d{1,2})(?:[.,](\d{1,3}))?$/.exec((texto || '').trim());
        if (!achado) {
            return null;
        }

        const horas = Number(achado[1] || 0);
        const minutos = Number(achado[2]);
        const segundos = Number(achado[3]);
        const milis = Number((achado[4] || '0').padEnd(3, '0'));

        if (minutos > 59 || segundos > 59) {
            return null;
        }

        return horas * 3600 + minutos * 60 + segundos + milis / 1000;
    }

    function formatarTempo(total) {
        const ms = Math.max(0, Math.round(total * 1000));
        const horas = Math.floor(ms / 3600000);
        const minutos = Math.floor(ms / 60000) % 60;
        const segundos = Math.floor(ms / 1000) % 60;
        const milis = ms % 1000;
        const dois = n => String(n).padStart(2, '0');

        return dois(horas) + ':' + dois(minutos) + ':' + dois(segundos) + '.' + String(milis).padStart(3, '0');
    }

    function montar() {
        const raiz = document.getElementById('editor-legendas');
        if (!raiz || raiz.dataset.montado) {
            return;
        }

        raiz.dataset.montado = '1';

        const linhas = raiz.querySelector('.editor-linhas');
        const modelo = document.getElementById('editor-modelo-linha');
        const situacao = raiz.querySelector('.editor-situacao');
        const contagem = raiz.querySelector('.editor-contagem');
        const listaDeErros = raiz.querySelector('.editor-erros');
        const video = document.getElementById('editor-video');
        const legendaAtual = document.querySelector('.editor-legenda-atual');
        const token = raiz.querySelector('input[name="__RequestVerificationToken"]');
        const msg = raiz.dataset;

        let alterado = false;
        let salvando = false;

        const todas = () => Array.from(linhas.querySelectorAll('.editor-linha'));
        const formatar = (modeloTexto, ...args) => modeloTexto.replace(/\{(\d+)\}/g, (_, i) => args[Number(i)]);

        function marcarAlterado() {
            alterado = true;
            situacao.textContent = msg.msgAlterado;
            situacao.classList.remove('salvo');
        }

        function renumerar() {
            todas().forEach((linha, i) => { linha.querySelector('.editor-numero').textContent = i + 1; });
            contagem.textContent = contagem.textContent.replace(/\d+/, todas().length);
        }

        function ajustarAltura(texto) {
            texto.style.height = 'auto';
            texto.style.height = texto.scrollHeight + 'px';
        }

        function criarLinha(inicio, fim, texto) {
            const linha = modelo.content.firstElementChild.cloneNode(true);
            linha.querySelector('.editor-inicio').value = formatarTempo(inicio);
            linha.querySelector('.editor-fim').value = formatarTempo(fim);
            linha.querySelector('.editor-texto').value = texto || '';
            return linha;
        }

        function inserirDepois(referencia) {
            const fimDaAnterior = referencia ? lerTempo(referencia.querySelector('.editor-fim').value) : null;
            const inicio = fimDaAnterior ?? (video && !isNaN(video.currentTime) ? video.currentTime : 0);
            const proxima = referencia ? referencia.nextElementSibling : null;
            const inicioDaProxima = proxima ? lerTempo(proxima.querySelector('.editor-inicio').value) : null;

            // A nova legenda cabe até a próxima, sem passar da duração padrão.
            let fim = inicio + DURACAO_PADRAO;
            if (inicioDaProxima !== null && inicioDaProxima > inicio && inicioDaProxima < fim) {
                fim = inicioDaProxima;
            }

            const nova = criarLinha(inicio, fim, '');

            if (referencia) {
                referencia.after(nova);
            } else {
                linhas.appendChild(nova);
            }

            renumerar();
            marcarAlterado();
            nova.querySelector('.editor-texto').focus();
            return nova;
        }

        // ── Validação e montagem do WebVTT ──────────────────────────────────
        function validar() {
            const erros = [];
            const trechos = [];

            todas().forEach((linha, i) => {
                const numero = i + 1;
                const campoInicio = linha.querySelector('.editor-inicio');
                const campoFim = linha.querySelector('.editor-fim');
                const campoTexto = linha.querySelector('.editor-texto');
                const inicio = lerTempo(campoInicio.value);
                const fim = lerTempo(campoFim.value);
                const texto = campoTexto.value.split('\n').map(l => l.trim()).filter(l => l.length > 0).join('\n');

                [campoInicio, campoFim, campoTexto].forEach(c => c.classList.remove('invalido'));

                if (inicio === null || fim === null) {
                    erros.push(formatar(msg.msgTempo, numero));
                    (inicio === null ? campoInicio : campoFim).classList.add('invalido');
                } else if (fim <= inicio) {
                    erros.push(formatar(msg.msgOrdem, numero));
                    campoFim.classList.add('invalido');
                }

                if (texto.length === 0) {
                    erros.push(formatar(msg.msgVazio, numero));
                    campoTexto.classList.add('invalido');
                } else if (texto.includes('-->')) {
                    erros.push(formatar(msg.msgSeta, numero));
                    campoTexto.classList.add('invalido');
                }

                trechos.push({ numero, inicio, fim, texto, ajustes: linha.dataset.ajustes || '' });
            });

            // Sobreposição é aviso, não erro: às vezes duas falas aparecem juntas de propósito.
            const avisos = [];
            for (let i = 1; i < trechos.length; i++) {
                const anterior = trechos[i - 1];
                if (anterior.fim !== null && trechos[i].inicio !== null && trechos[i].inicio < anterior.fim) {
                    avisos.push(formatar(msg.msgSobreposto, trechos[i].numero, anterior.numero));
                }
            }

            listaDeErros.replaceChildren(...erros.concat(avisos).map((texto, i) => {
                const item = document.createElement('li');
                item.textContent = texto;
                item.className = i < erros.length ? 'erro' : 'aviso';
                return item;
            }));
            listaDeErros.hidden = erros.length + avisos.length === 0;

            return { erros, trechos };
        }

        function paraWebVtt(trechos) {
            const blocos = trechos
                .slice()
                .sort((a, b) => a.inicio - b.inicio || a.fim - b.fim)
                .map(t => formatarTempo(t.inicio) + ' --> ' + formatarTempo(t.fim) + (t.ajustes ? ' ' + t.ajustes : '') + '\n' + t.texto);

            return 'WEBVTT\n' + blocos.map(b => '\n' + b + '\n').join('');
        }

        async function salvar() {
            if (salvando) {
                return;
            }

            const { erros, trechos } = validar();
            if (erros.length > 0) {
                listaDeErros.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
                return;
            }

            salvando = true;
            situacao.textContent = msg.msgSalvando;

            try {
                const resposta = await fetch(raiz.dataset.salvar, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token ? token.value : '' },
                    body: JSON.stringify({ conteudo: paraWebVtt(trechos) })
                });
                const corpo = await resposta.json().catch(() => ({}));

                if (!resposta.ok) {
                    situacao.textContent = corpo.erro || msg.msgFalha;
                    situacao.classList.remove('salvo');
                    return;
                }

                alterado = false;
                situacao.textContent = formatar(msg.msgSalvo, new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }));
                situacao.classList.add('salvo');
            } catch (_) {
                situacao.textContent = msg.msgFalha;
            } finally {
                salvando = false;
            }
        }

        // ── Ligação com o vídeo ─────────────────────────────────────────────
        function irPara(linha, tocar) {
            const inicio = lerTempo(linha.querySelector('.editor-inicio').value);
            if (!video || inicio === null) {
                return;
            }

            video.currentTime = inicio;
            if (tocar) {
                video.play().catch(() => { });
            }

            // O destaque não espera o vídeo carregar até o ponto: responde ao clique na hora.
            acompanharVideo();
        }

        function marcarTempo(linha, campo) {
            if (!video || isNaN(video.currentTime)) {
                return;
            }

            linha.querySelector(campo).value = formatarTempo(video.currentTime);
            marcarAlterado();
            validar();
        }

        let linhaTocando = null;

        function acompanharVideo() {
            if (!video) {
                return;
            }

            const agora = video.currentTime;
            const atual = todas().find(linha => {
                const inicio = lerTempo(linha.querySelector('.editor-inicio').value);
                const fim = lerTempo(linha.querySelector('.editor-fim').value);
                return inicio !== null && fim !== null && agora >= inicio && agora < fim;
            }) || null;

            if (atual !== linhaTocando) {
                if (linhaTocando) {
                    linhaTocando.classList.remove('tocando');
                }
                if (atual) {
                    atual.classList.add('tocando');
                    // Acompanha a reprodução sem roubar a rolagem de quem está digitando.
                    if (!video.paused && !raiz.contains(document.activeElement)) {
                        atual.scrollIntoView({ block: 'nearest' });
                    }
                }
                linhaTocando = atual;
            }

            if (legendaAtual) {
                legendaAtual.textContent = atual ? atual.querySelector('.editor-texto').value : '';
                legendaAtual.hidden = !atual;
            }
        }

        // ── Eventos ─────────────────────────────────────────────────────────
        raiz.addEventListener('click', function (e) {
            const botao = e.target.closest('[data-acao]');
            if (!botao) {
                return;
            }

            const linha = botao.closest('.editor-linha');

            switch (botao.dataset.acao) {
                case 'salvar': salvar(); break;
                case 'adicionar': inserirDepois(todas().at(-1) || null); break;
                case 'inserir': inserirDepois(linha); break;
                case 'ir': irPara(linha, false); break;
                case 'tocar': irPara(linha, true); break;
                case 'marcar-inicio': marcarTempo(linha, '.editor-inicio'); break;
                case 'marcar-fim': marcarTempo(linha, '.editor-fim'); break;
                case 'remover': {
                    const vizinha = linha.nextElementSibling || linha.previousElementSibling;
                    linha.remove();
                    renumerar();
                    marcarAlterado();
                    validar();
                    if (vizinha) {
                        vizinha.querySelector('.editor-texto').focus();
                    }
                    break;
                }
            }
        });

        raiz.addEventListener('input', function (e) {
            if (e.target.matches('.editor-texto')) {
                ajustarAltura(e.target);
            }
            if (e.target.matches('input, textarea')) {
                marcarAlterado();
                acompanharVideo();
            }
        });

        // Ao sair do campo, o tempo é reescrito no formato completo.
        raiz.addEventListener('focusout', function (e) {
            if (e.target.matches('.editor-inicio, .editor-fim')) {
                const tempo = lerTempo(e.target.value);
                if (tempo !== null) {
                    e.target.value = formatarTempo(tempo);
                }
                validar();
            }
        });

        raiz.addEventListener('focusin', function (e) {
            todas().forEach(l => l.classList.toggle('ativa', l.contains(e.target)));
        });

        raiz.addEventListener('keydown', function (e) {
            const linha = e.target.closest('.editor-linha');
            const comando = e.metaKey || e.ctrlKey;

            if (comando && e.key.toLowerCase() === 's') {
                e.preventDefault();
                salvar();
            } else if (comando && e.key === 'Enter' && linha) {
                e.preventDefault();
                inserirDepois(linha);
            } else if (e.altKey && (e.key === 'ArrowUp' || e.key === 'ArrowDown') && linha) {
                e.preventDefault();
                const destino = e.key === 'ArrowUp' ? linha.previousElementSibling : linha.nextElementSibling;
                if (destino) {
                    const classe = e.target.classList.contains('editor-texto') ? '.editor-texto'
                        : e.target.classList.contains('editor-fim') ? '.editor-fim' : '.editor-inicio';
                    destino.querySelector(classe).focus();
                }
            } else if (e.altKey && e.code === 'BracketLeft' && linha) {
                e.preventDefault();
                marcarTempo(linha, '.editor-inicio');
            } else if (e.altKey && e.code === 'BracketRight' && linha) {
                e.preventDefault();
                marcarTempo(linha, '.editor-fim');
            } else if (e.altKey && e.code === 'KeyP' && video) {
                e.preventDefault();
                if (video.paused) {
                    video.play().catch(() => { });
                } else {
                    video.pause();
                }
            } else if (e.key === 'Enter' && e.target.matches('.editor-inicio, .editor-fim')) {
                e.preventDefault();
                linha.querySelector('.editor-texto').focus();
            }
        });

        window.addEventListener('beforeunload', function (e) {
            if (alterado && raiz.isConnected) {
                e.preventDefault();
                e.returnValue = msg.msgSair;
            }
        });

        if (video) {
            video.addEventListener('timeupdate', acompanharVideo);
            video.addEventListener('seeked', acompanharVideo);
            video.addEventListener('play', acompanharVideo);
        }

        linhas.querySelectorAll('.editor-texto').forEach(ajustarAltura);
        validar();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', montar);
    } else {
        montar();
    }

    if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
        window.Blazor.addEventListener('enhancedload', montar);
    }

    return { montar: montar, lerTempo: lerTempo, formatarTempo: formatarTempo };
})();
