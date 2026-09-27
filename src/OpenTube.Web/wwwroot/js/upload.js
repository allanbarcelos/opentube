// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

// Envio de vídeos direto do navegador para o storage, em pedaços assinados pela aplicação.
// O servidor nunca recebe os bytes do vídeo: só coordena o envio.
//
// Aceita vários arquivos ou uma pasta inteira. Cada vídeo ganha como título o nome do arquivo
// sem a extensão (editável na fila). Uma pasta vira uma coleção com o nome dela, e cada vídeo
// entra na coleção ao terminar de subir. Os arquivos sobem um por vez, na ordem da fila.
//
// Carregado para todas as páginas no App.razor; monta-se sozinho quando a página de envio
// aparece, inclusive pela navegação aprimorada do Blazor.
window.envioDeVideos = (function () {
    const LOTE_DE_ASSINATURAS = 50;

    // A mesma lista do servidor (MediaTypes): o navegador nem sempre sabe o tipo de .mkv ou .mts.
    const EXTENSOES = ['mp4', 'mov', 'mkv', 'webm', 'avi', 'm4v', 'mpg', 'mpeg', 'wmv', 'flv', 'ts', 'mts', 'm2ts'];

    const ordenar = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

    function formatar(modelo, ...valores) {
        return valores.reduce((texto, valor, i) => texto.replace('{' + i + '}', valor), modelo || '');
    }

    function ehVideo(arquivo) {
        if (arquivo.type && arquivo.type.startsWith('video/')) {
            return true;
        }
        const ponto = arquivo.name.lastIndexOf('.');
        return ponto > 0 && EXTENSOES.includes(arquivo.name.slice(ponto + 1).toLowerCase());
    }

    // Mesma regra do servidor (UploadNames): o nome do arquivo sem a extensão.
    function tituloDoArquivo(nome, limite) {
        const ponto = nome.lastIndexOf('.');
        const semExtensao = (ponto > 0 ? nome.slice(0, ponto) : nome).trim();
        const titulo = semExtensao || nome.trim();
        return titulo.length > limite ? titulo.slice(0, limite).trim() : titulo;
    }

    function tamanhoLegivel(bytes) {
        const unidades = ['B', 'KB', 'MB', 'GB', 'TB'];
        let valor = bytes;
        let i = 0;
        while (valor >= 1024 && i < unidades.length - 1) {
            valor /= 1024;
            i++;
        }
        const numero = new Intl.NumberFormat(document.documentElement.lang || undefined,
            { maximumFractionDigits: i === 0 ? 0 : 1 }).format(valor);
        return numero + ' ' + unidades[i];
    }

    function montar() {
        const formulario = document.getElementById('formulario-envio');
        if (!formulario || formulario.dataset.montado === '1') {
            return;
        }
        formulario.dataset.montado = '1';

        const textos = formulario.dataset;
        const token = textos.token;
        const limiteTitulo = parseInt(textos.maxTitulo, 10) || 300;
        const limiteColecao = parseInt(textos.maxColecao, 10) || 200;

        const campoArquivos = document.getElementById('arquivos');
        const campoPasta = document.getElementById('pasta');
        const blocoColecao = document.getElementById('bloco-colecao');
        const campoColecao = document.getElementById('nome-colecao');
        const campoDescricao = document.getElementById('descricao');
        const blocoFila = document.getElementById('bloco-fila');
        const corpoFila = document.getElementById('fila-envio');
        const resumoFila = document.getElementById('resumo-fila');
        const progressoGeral = document.getElementById('progresso-geral');
        const ignorados = document.getElementById('arquivos-ignorados');
        const erro = document.getElementById('erro-envio');
        const conclusao = document.getElementById('conclusao-envio');
        const botao = document.getElementById('botao-enviar');
        const modelo = document.getElementById('modelo-linha-envio');

        let fila = [];
        let pasta = null;
        let colecao = null;
        let enviando = false;

        async function postar(url, corpo) {
            const resposta = await fetch(url, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
                body: JSON.stringify(corpo)
            });

            if (!resposta.ok) {
                let mensagem = textos.falhaServidor;
                try {
                    const dados = await resposta.json();
                    mensagem = dados.erro || dados.detail || mensagem;
                } catch (_) {
                    // Resposta sem corpo em JSON; fica a mensagem genérica.
                }
                throw new Error(mensagem);
            }

            return resposta.status === 204 ? null : await resposta.json();
        }

        async function enviarPedaco(url, pedaco) {
            const resposta = await fetch(url, { method: 'PUT', body: pedaco });
            if (!resposta.ok) {
                throw new Error(textos.falhaPedaco);
            }
            const etag = resposta.headers.get('ETag');
            if (!etag) {
                throw new Error(textos.falhaStorage);
            }
            return etag;
        }

        function mostrarErro(mensagem) {
            erro.textContent = mensagem;
            erro.classList.toggle('d-none', !mensagem);
        }

        function situacao(item, tipo, texto, percentual) {
            const celula = item.linha.querySelector('[data-campo="situacao"]');
            celula.replaceChildren();

            if (tipo === 'progresso') {
                const barra = document.createElement('div');
                barra.className = 'progress';
                barra.setAttribute('role', 'progressbar');
                barra.setAttribute('aria-valuemin', '0');
                barra.setAttribute('aria-valuemax', '100');
                barra.setAttribute('aria-valuenow', String(percentual));
                barra.style.height = '0.5rem';
                const preenchido = document.createElement('div');
                preenchido.className = 'progress-bar';
                preenchido.style.width = percentual + '%';
                barra.append(preenchido);

                const legenda = document.createElement('div');
                legenda.className = 'text-body-secondary mt-1';
                legenda.textContent = texto;
                celula.append(barra, legenda);
                return;
            }

            const selo = document.createElement('span');
            selo.className = 'badge ' + ({
                aguardando: 'text-bg-secondary',
                concluido: 'text-bg-success',
                falhou: 'text-bg-danger'
            })[tipo];
            selo.textContent = texto;
            celula.append(selo);

            if (tipo === 'concluido' && item.destino) {
                const link = document.createElement('a');
                link.className = 'ms-2';
                link.href = item.destino;
                link.target = '_blank';
                link.rel = 'noopener';
                link.textContent = textos.abrir;
                celula.append(link);
            }

            if (tipo === 'falhou' && item.erro) {
                const motivo = document.createElement('div');
                motivo.className = 'text-danger mt-1';
                motivo.textContent = item.erro;
                celula.append(motivo);
            }
        }

        function atualizarResumo() {
            const total = fila.reduce((soma, item) => soma + item.arquivo.size, 0);
            resumoFila.textContent = formatar(textos.resumoFila, fila.length, tamanhoLegivel(total));
            blocoFila.classList.toggle('d-none', fila.length === 0);

            const enviados = fila.filter(item => item.concluido).length;
            progressoGeral.textContent = enviados > 0 ? formatar(textos.progressoGeral, enviados, fila.length) : '';

            fila.forEach((item, i) => {
                item.linha.querySelector('[data-campo="numero"]').textContent = String(i + 1);
            });
        }

        function montarFila(arquivos) {
            fila = [];
            colecao = null;
            campoColecao.disabled = false;
            corpoFila.replaceChildren();
            mostrarErro('');
            conclusao.classList.add('d-none');

            const videos = [];
            const outros = [];
            for (const arquivo of arquivos) {
                // Arquivos ocultos (.DS_Store, Thumbs.db) não interessam nem como aviso.
                if (arquivo.name.startsWith('.') || arquivo.name === 'Thumbs.db') {
                    continue;
                }
                (ehVideo(arquivo) ? videos : outros).push(arquivo);
            }

            const caminho = arquivo => arquivo.webkitRelativePath || arquivo.name;
            videos.sort((a, b) => ordenar.compare(caminho(a), caminho(b)));

            for (const arquivo of videos) {
                const linha = modelo.content.firstElementChild.cloneNode(true);
                const item = { arquivo: arquivo, linha: linha, concluido: false };

                const titulo = linha.querySelector('[data-campo="titulo"]');
                titulo.value = tituloDoArquivo(arquivo.name, limiteTitulo);

                const onde = linha.querySelector('[data-campo="caminho"]');
                onde.textContent = caminho(arquivo);
                onde.title = caminho(arquivo);

                linha.querySelector('[data-campo="tamanho"]').textContent = tamanhoLegivel(arquivo.size);

                const remover = linha.querySelector('[data-campo="remover"]');
                remover.title = textos.remover;
                remover.setAttribute('aria-label', textos.remover);
                remover.addEventListener('click', function () {
                    if (enviando || item.concluido) {
                        return;
                    }
                    fila = fila.filter(outro => outro !== item);
                    linha.remove();
                    atualizarResumo();
                });

                item.titulo = titulo;
                fila.push(item);
                corpoFila.append(linha);
                situacao(item, 'aguardando', textos.aguardando);
            }

            if (outros.length > 0) {
                const nomes = outros.slice(0, 5).map(caminho).join(', ') + (outros.length > 5 ? '…' : '');
                ignorados.textContent = formatar(textos.ignorados, outros.length, nomes);
                ignorados.classList.remove('d-none');
            } else {
                ignorados.classList.add('d-none');
            }

            atualizarResumo();
            return videos.length;
        }

        campoArquivos.addEventListener('change', function () {
            if (enviando) {
                return;
            }
            campoPasta.value = '';
            pasta = null;
            blocoColecao.classList.add('d-none');
            montarFila(campoArquivos.files);
        });

        campoPasta.addEventListener('change', function () {
            if (enviando) {
                return;
            }
            campoArquivos.value = '';
            const arquivos = Array.from(campoPasta.files);
            const primeiro = arquivos.find(a => a.webkitRelativePath);

            pasta = primeiro ? primeiro.webkitRelativePath.split('/')[0] : null;
            blocoColecao.classList.toggle('d-none', !pasta);
            campoColecao.value = pasta ? pasta.slice(0, limiteColecao) : '';

            const quantos = montarFila(arquivos);
            if (pasta && quantos === 0) {
                mostrarErro(textos.pastaVazia);
            }
        });

        async function enviarUm(item) {
            const arquivo = item.arquivo;
            let bilhete = null;

            situacao(item, 'progresso', textos.preparando, 0);

            try {
                bilhete = await postar('/api/admin/uploads/start', {
                    titulo: item.titulo.value.trim() || tituloDoArquivo(arquivo.name, limiteTitulo),
                    descricao: campoDescricao.value,
                    arquivo: arquivo.name,
                    tipo: arquivo.type,
                    tamanho: arquivo.size,
                    colecaoId: colecao ? colecao.colecaoId : null
                });

                const enviados = [];
                let assinadas = bilhete.partes;

                for (let numero = 1; numero <= bilhete.totalDePedacos; numero++) {
                    if (!assinadas.some(p => p.numero === numero)) {
                        const lote = await postar(`/api/admin/uploads/${bilhete.videoId}/parts`, {
                            uploadId: bilhete.uploadId,
                            primeira: numero,
                            quantidade: Math.min(LOTE_DE_ASSINATURAS, bilhete.totalDePedacos - numero + 1)
                        });
                        assinadas = lote.partes;
                    }

                    const parte = assinadas.find(p => p.numero === numero);
                    const inicio = (numero - 1) * bilhete.tamanhoDoPedaco;
                    const fim = Math.min(inicio + bilhete.tamanhoDoPedaco, arquivo.size);

                    const etag = await enviarPedaco(parte.url, arquivo.slice(inicio, fim));
                    enviados.push({ numero: numero, eTag: etag });

                    const percentual = Math.round((numero / bilhete.totalDePedacos) * 100);
                    situacao(item, 'progresso', formatar(textos.enviando, percentual), percentual);
                }

                situacao(item, 'progresso', textos.finalizando, 100);

                const resultado = await postar(`/api/admin/uploads/${bilhete.videoId}/complete`, {
                    uploadId: bilhete.uploadId,
                    partes: enviados,
                    colecaoId: colecao ? colecao.colecaoId : null
                });

                item.concluido = true;
                item.erro = null;
                item.destino = resultado.destino;
                item.titulo.disabled = true;
                item.linha.querySelector('[data-campo="remover"]').classList.add('invisible');
                situacao(item, 'concluido', textos.concluido);
            } catch (falha) {
                item.erro = falha.message;
                situacao(item, 'falhou', textos.falhou);

                if (bilhete) {
                    // Libera os pedaços já recebidos em vez de deixá-los ocupando espaço.
                    try {
                        await postar(`/api/admin/uploads/${bilhete.videoId}/cancel`, { uploadId: bilhete.uploadId });
                    } catch (_) {
                        // Nada a fazer: o storage descarta envios incompletos por conta própria.
                    }
                }
            }

            atualizarResumo();
        }

        function avisarSaida(evento) {
            evento.preventDefault();
            evento.returnValue = textos.sair;
            return textos.sair;
        }

        function travar(travado) {
            enviando = travado;
            botao.disabled = travado;
            campoArquivos.disabled = travado;
            campoPasta.disabled = travado;
            campoColecao.disabled = travado || colecao !== null;
            campoDescricao.disabled = travado;
            fila.forEach(item => {
                item.titulo.disabled = travado || item.concluido;
                item.linha.querySelector('[data-campo="remover"]').disabled = travado;
            });

            if (travado) {
                window.addEventListener('beforeunload', avisarSaida);
            } else {
                window.removeEventListener('beforeunload', avisarSaida);
            }
        }

        function concluir() {
            const enviados = fila.filter(item => item.concluido);
            const falhas = fila.length - enviados.length;

            // Um vídeo só, sem pasta: segue direto para a página dele, como sempre foi.
            if (!pasta && fila.length === 1 && falhas === 0) {
                window.location.href = enviados[0].destino;
                return;
            }

            conclusao.replaceChildren();
            conclusao.classList.toggle('alert-success', falhas === 0);
            conclusao.classList.toggle('alert-warning', falhas > 0);

            const frase = document.createElement('div');
            frase.textContent = formatar(textos.conclusao, enviados.length, fila.length)
                + (falhas > 0 ? ' ' + formatar(textos.comFalhas, falhas) : '');
            conclusao.append(frase);

            const links = document.createElement('div');
            links.className = 'mt-2 d-flex flex-wrap gap-3';
            if (colecao) {
                const link = document.createElement('a');
                link.href = colecao.destino;
                link.textContent = textos.abrirColecao + ': ' + colecao.nome;
                links.append(link);
            }
            const videos = document.createElement('a');
            videos.href = '/admin';
            videos.textContent = textos.verVideos;
            links.append(videos);
            conclusao.append(links);

            conclusao.classList.remove('d-none');
        }

        formulario.addEventListener('submit', async function (evento) {
            evento.preventDefault();
            if (enviando) {
                return;
            }

            mostrarErro('');
            conclusao.classList.add('d-none');

            // Um novo "Enviar" depois de falhas tenta de novo só o que não subiu.
            const pendentes = fila.filter(item => !item.concluido);
            if (pendentes.length === 0) {
                mostrarErro(fila.length === 0 ? (pasta ? textos.pastaVazia : textos.semArquivos) : '');
                return;
            }

            if (pasta && !colecao && !campoColecao.value.trim()) {
                mostrarErro(textos.semColecao);
                campoColecao.focus();
                return;
            }

            travar(true);

            try {
                if (pasta && !colecao) {
                    colecao = await postar('/api/admin/uploads/collection', {
                        nome: campoColecao.value.trim(),
                        descricao: null
                    });
                    campoColecao.value = colecao.nome;
                }

                for (const item of pendentes) {
                    await enviarUm(item);
                }
            } catch (falha) {
                mostrarErro(falha.message);
            } finally {
                travar(false);
            }

            if (fila.some(item => item.concluido)) {
                concluir();
            }
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', montar);
    } else {
        montar();
    }

    if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
        window.Blazor.addEventListener('enhancedload', montar);
    }

    return { montar: montar, tituloDoArquivo: tituloDoArquivo };
})();
