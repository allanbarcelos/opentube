// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

// Envio de vídeos direto do navegador para o storage, em pedaços assinados pela aplicação.
// O servidor nunca recebe os bytes do vídeo: só coordena o envio.
//
// Uma área só recebe tudo: clicar escolhe vídeos, o link escolhe uma pasta, e arrastar (para a
// área ou para qualquer ponto da página) aceita vídeos, pastas ou os dois. O envio começa assim
// que o arquivo entra na fila, um por vez, e cada linha se cancela, se tira da fila ou se tenta
// de novo sozinha. Cada vídeo ganha como título o nome do arquivo sem a extensão, editável até
// terminar de subir. Uma pasta sozinha vira uma coleção com o nome dela, criada quando o primeiro
// vídeo termina; várias pastas, ou pastas junto com arquivos, entram como vídeos soltos.
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

    // Uma pasta solta: todos os arquivos dela, subpastas incluídas, com o caminho a partir dela.
    // O navegador entrega o conteúdo em lotes; lê até vir um lote vazio.
    async function lerPasta(entrada) {
        const arquivos = [];
        const leitor = entrada.createReader();

        for (;;) {
            const lote = await new Promise((resolve, reject) => leitor.readEntries(resolve, reject));
            if (lote.length === 0) {
                break;
            }
            for (const item of lote) {
                if (item.isDirectory) {
                    arquivos.push(...await lerPasta(item));
                } else if (item.isFile) {
                    const arquivo = await new Promise((resolve, reject) => item.file(resolve, reject));
                    arquivos.push({ arquivo: arquivo, caminho: item.fullPath.replace(/^\//, '') });
                }
            }
        }

        return arquivos;
    }

    // O que foi solto: as entradas precisam ser pegas durante o evento; o conteúdo, depois.
    function entradasSoltas(dados) {
        const itens = Array.from(dados.items || []).filter(item => item.kind === 'file');
        const entradas = itens.map(item => item.webkitGetAsEntry ? item.webkitGetAsEntry() : null);

        if (entradas.length > 0 && entradas.every(Boolean)) {
            return entradas;
        }

        // Navegador sem acesso a pastas no arrastar: só os arquivos.
        return Array.from(dados.files || []).map(arquivo => ({ isFile: true, arquivoPronto: arquivo }));
    }

    // Quem trata o que for solto na página de envio montada agora. Os ouvintes do documento são
    // instalados uma vez só; sem a página, não interferem no arrastar do resto do site.
    let soltarNaPagina = null;

    function arrastandoArquivos(evento) {
        return soltarNaPagina && soltarNaPagina.ativo()
            && evento.dataTransfer && Array.from(evento.dataTransfer.types || []).includes('Files');
    }

    let camadas = 0;

    document.addEventListener('dragenter', function (evento) {
        if (!arrastandoArquivos(evento)) {
            return;
        }
        evento.preventDefault();
        camadas++;
        soltarNaPagina.destacar(true);
    });

    document.addEventListener('dragover', function (evento) {
        if (!arrastandoArquivos(evento)) {
            return;
        }
        // Sem isto o navegador abriria o vídeo no lugar da página, perdendo a fila.
        evento.preventDefault();
        evento.dataTransfer.dropEffect = soltarNaPagina.travada() ? 'none' : 'copy';
    });

    document.addEventListener('dragleave', function (evento) {
        if (!arrastandoArquivos(evento)) {
            return;
        }
        camadas = Math.max(0, camadas - 1);
        if (camadas === 0) {
            soltarNaPagina.destacar(false);
        }
    });

    document.addEventListener('drop', function (evento) {
        if (!arrastandoArquivos(evento)) {
            return;
        }
        evento.preventDefault();
        camadas = 0;
        soltarNaPagina.destacar(false);
        soltarNaPagina.receber(entradasSoltas(evento.dataTransfer));
    });

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
        const area = document.getElementById('area-de-envio');
        const blocoFila = document.getElementById('bloco-fila');
        const corpoFila = document.getElementById('fila-envio');
        const resumoFila = document.getElementById('resumo-fila');
        const progressoGeral = document.getElementById('progresso-geral');
        const ignorados = document.getElementById('arquivos-ignorados');
        const erro = document.getElementById('erro-envio');
        const conclusao = document.getElementById('conclusao-envio');
        const modelo = document.getElementById('modelo-linha-envio');
        const modeloGrupo = document.getElementById('modelo-grupo-envio');

        // Cada item: { arquivo, caminho, linha, titulo, grupo, estado, controle, destino, erro }.
        // Estados: aguardando, enviando, concluido, falhou. Um cancelado sai da fila.
        let fila = [];
        let processando = false;

        // O formulário não é enviado: Enter num título não pode recarregar a página.
        formulario.addEventListener('submit', evento => evento.preventDefault());

        async function postar(url, corpo, sinal) {
            const resposta = await fetch(url, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
                body: JSON.stringify(corpo),
                signal: sinal
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

        async function enviarPedaco(url, pedaco, sinal) {
            const resposta = await fetch(url, { method: 'PUT', body: pedaco, signal: sinal });
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

        // Situação e ações de uma linha: o X tira da fila enquanto espera, cancela enquanto
        // sobe e some depois de enviado; a seta tenta de novo o que falhou.
        function situacao(item, tipo, texto, percentual) {
            item.estado = tipo === 'progresso' ? 'enviando' : tipo;

            const celula = item.linha.querySelector('[data-campo="situacao"]');
            celula.replaceChildren();

            const remover = item.linha.querySelector('[data-campo="remover"]');
            const tentar = item.linha.querySelector('[data-campo="tentar"]');
            const rotulo = item.estado === 'enviando' ? textos.cancelarEnvio : textos.remover;
            remover.title = rotulo;
            remover.setAttribute('aria-label', rotulo);
            remover.classList.toggle('d-none', item.estado === 'concluido');
            tentar.classList.toggle('d-none', item.estado !== 'falhou');
            item.titulo.disabled = item.estado === 'concluido';

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

            const enviados = fila.filter(item => item.estado === 'concluido').length;
            progressoGeral.textContent = enviados > 0 ? formatar(textos.progressoGeral, enviados, fila.length) : '';

            fila.forEach((item, i) => {
                item.linha.querySelector('[data-campo="numero"]').textContent = String(i + 1);
            });

            // Uma pasta que ficou sem vídeos, e cuja coleção nem chegou a nascer, sai da lista.
            corpoFila.querySelectorAll('[data-grupo]').forEach(linha => {
                if (!linha.grupo.colecao && !fila.some(item => item.grupo === linha.grupo)) {
                    linha.remove();
                }
            });
        }

        // Cada entrada é { arquivo, caminho }: o caminho vem da pasta escolhida ou solta.
        // Mesmo arquivo escolhido de novo não entra duas vezes na fila.
        const chave = entrada => [entrada.caminho, entrada.arquivo.size, entrada.arquivo.lastModified].join('|');

        // Acrescenta à fila e já começa a enviar. Devolve quantos vídeos entraram.
        function acrescentar(entradas, grupo) {
            mostrarErro('');
            conclusao.classList.add('d-none');

            const naFila = new Set(fila.map(item => chave(item)));

            const videos = [];
            const outros = [];
            for (const entrada of entradas) {
                const arquivo = entrada.arquivo;
                // Arquivos ocultos (.DS_Store, Thumbs.db) não interessam nem como aviso.
                if (arquivo.name.startsWith('.') || arquivo.name === 'Thumbs.db') {
                    continue;
                }
                if (naFila.has(chave(entrada))) {
                    continue;
                }
                naFila.add(chave(entrada));
                (ehVideo(arquivo) ? videos : outros).push(entrada);
            }

            videos.sort((a, b) => ordenar.compare(a.caminho, b.caminho));

            if (grupo && videos.length > 0) {
                corpoFila.append(grupo.linha);
            }

            for (const entrada of videos) {
                const arquivo = entrada.arquivo;
                const linha = modelo.content.firstElementChild.cloneNode(true);
                const item = { arquivo: arquivo, caminho: entrada.caminho, linha: linha, grupo: grupo, estado: 'aguardando' };

                const titulo = linha.querySelector('[data-campo="titulo"]');
                titulo.value = tituloDoArquivo(arquivo.name, limiteTitulo);
                item.titulo = titulo;

                const onde = linha.querySelector('[data-campo="caminho"]');
                onde.textContent = entrada.caminho;
                onde.title = entrada.caminho;

                linha.querySelector('[data-campo="tamanho"]').textContent = tamanhoLegivel(arquivo.size);

                linha.querySelector('[data-campo="remover"]').addEventListener('click', () => remover(item));
                linha.querySelector('[data-campo="tentar"]').addEventListener('click', function () {
                    situacao(item, 'aguardando', textos.aguardando);
                    processar();
                });

                fila.push(item);
                corpoFila.append(linha);
                situacao(item, 'aguardando', textos.aguardando);
            }

            if (outros.length > 0) {
                const nomes = outros.slice(0, 5).map(entrada => entrada.caminho).join(', ') + (outros.length > 5 ? '…' : '');
                ignorados.textContent = formatar(textos.ignorados, outros.length, nomes);
                ignorados.classList.remove('d-none');
            } else {
                ignorados.classList.add('d-none');
            }

            atualizarResumo();
            processar();
            return videos.length;
        }

        // O X de uma linha: fora da fila se ainda não subiu; interrompido se estiver subindo.
        function remover(item) {
            if (item.estado === 'concluido') {
                return;
            }
            if (item.estado === 'enviando' && item.controle) {
                item.cancelado = true;
                item.controle.abort();
            }
            fila = fila.filter(outro => outro !== item);
            item.linha.remove();
            atualizarResumo();
        }

        // Uma pasta: um grupo na fila, com o nome da coleção editável até ela ser criada.
        function novoGrupo(nome) {
            const linha = modeloGrupo.content.firstElementChild.cloneNode(true);
            const campo = linha.querySelector('[data-campo="colecao"]');
            campo.value = nome.slice(0, limiteColecao);
            const grupo = { pasta: nome, linha: linha, campo: campo, colecao: null, criando: null };
            linha.grupo = grupo;
            return grupo;
        }

        // A coleção nasce quando o primeiro vídeo da pasta termina de subir, com o nome escrito.
        async function colecaoDo(grupo, sinal) {
            if (grupo.colecao) {
                return grupo.colecao;
            }
            if (!grupo.criando) {
                const nome = grupo.campo.value.trim() || grupo.pasta;
                grupo.criando = postar('/api/admin/uploads/collection', { nome: nome.slice(0, limiteColecao), descricao: null }, sinal)
                    .then(colecao => {
                        grupo.colecao = colecao;
                        grupo.campo.value = colecao.nome;
                        grupo.campo.disabled = true;
                        const link = grupo.linha.querySelector('[data-campo="link-colecao"]');
                        link.href = colecao.destino;
                        link.textContent = textos.abrirColecao;
                        link.classList.remove('d-none');
                        grupo.linha.querySelector('[data-campo="ajuda-colecao"]').classList.add('d-none');
                        return colecao;
                    })
                    .finally(() => { grupo.criando = null; });
            }
            return grupo.criando;
        }

        function adicionarVideos(entradas) {
            acrescentar(entradas, null);
        }

        function adicionarPasta(nome, entradas) {
            if (acrescentar(entradas, novoGrupo(nome)) === 0) {
                mostrarErro(textos.pastaVazia);
            }
        }

        async function enviarUm(item) {
            const arquivo = item.arquivo;
            const controle = new AbortController();
            const sinal = controle.signal;
            let bilhete = null;

            item.controle = controle;
            item.cancelado = false;
            item.erro = null;
            situacao(item, 'progresso', textos.preparando, 0);

            try {
                bilhete = await postar('/api/admin/uploads/start', {
                    titulo: item.titulo.value.trim() || tituloDoArquivo(arquivo.name, limiteTitulo),
                    descricao: null,
                    arquivo: arquivo.name,
                    tipo: arquivo.type,
                    tamanho: arquivo.size,
                    colecaoId: null
                }, sinal);

                const enviados = [];
                let assinadas = bilhete.partes;

                for (let numero = 1; numero <= bilhete.totalDePedacos; numero++) {
                    if (!assinadas.some(p => p.numero === numero)) {
                        const lote = await postar(`/api/admin/uploads/${bilhete.videoId}/parts`, {
                            uploadId: bilhete.uploadId,
                            primeira: numero,
                            quantidade: Math.min(LOTE_DE_ASSINATURAS, bilhete.totalDePedacos - numero + 1)
                        }, sinal);
                        assinadas = lote.partes;
                    }

                    const parte = assinadas.find(p => p.numero === numero);
                    const inicio = (numero - 1) * bilhete.tamanhoDoPedaco;
                    const fim = Math.min(inicio + bilhete.tamanhoDoPedaco, arquivo.size);

                    const etag = await enviarPedaco(parte.url, arquivo.slice(inicio, fim), sinal);
                    enviados.push({ numero: numero, eTag: etag });

                    const percentual = Math.round((numero / bilhete.totalDePedacos) * 100);
                    situacao(item, 'progresso', formatar(textos.enviando, percentual), percentual);
                }

                situacao(item, 'progresso', textos.finalizando, 100);

                const colecao = item.grupo ? await colecaoDo(item.grupo, sinal) : null;

                // O título vai de novo: pode ter sido editado enquanto o arquivo subia.
                const resultado = await postar(`/api/admin/uploads/${bilhete.videoId}/complete`, {
                    uploadId: bilhete.uploadId,
                    partes: enviados,
                    colecaoId: colecao ? colecao.colecaoId : null,
                    titulo: item.titulo.value.trim() || null
                }, sinal);

                item.destino = resultado.destino;
                situacao(item, 'concluido', textos.concluido);
            } catch (falha) {
                if (bilhete) {
                    // Libera os pedaços já recebidos e apaga o rascunho, em vez de deixá-los
                    // ocupando espaço. Sem o sinal: o cancelamento já interrompeu o resto.
                    postar(`/api/admin/uploads/${bilhete.videoId}/cancel`, { uploadId: bilhete.uploadId })
                        .catch(() => { /* o storage descarta envios incompletos por conta própria */ });
                }
                if (!item.cancelado) {
                    item.erro = falha.message;
                    situacao(item, 'falhou', textos.falhou);
                }
            } finally {
                item.controle = null;
            }

            atualizarResumo();
        }

        function avisarSaida(evento) {
            evento.preventDefault();
            evento.returnValue = textos.sair;
            return textos.sair;
        }

        // Um arquivo por vez, na ordem da fila; o que entra durante o envio espera a sua vez.
        async function processar() {
            if (processando) {
                return;
            }
            processando = true;
            window.addEventListener('beforeunload', avisarSaida);

            try {
                let proximo;
                while ((proximo = fila.find(item => item.estado === 'aguardando'))) {
                    await enviarUm(proximo);
                }
            } finally {
                processando = false;
                window.removeEventListener('beforeunload', avisarSaida);
            }

            concluir();
        }

        function concluir() {
            const enviados = fila.filter(item => item.estado === 'concluido');
            if (enviados.length === 0) {
                conclusao.classList.add('d-none');
                return;
            }
            const falhas = fila.filter(item => item.estado === 'falhou').length;

            conclusao.replaceChildren();
            conclusao.classList.toggle('alert-success', falhas === 0);
            conclusao.classList.toggle('alert-warning', falhas > 0);

            const frase = document.createElement('div');
            frase.textContent = formatar(textos.conclusao, enviados.length, fila.length)
                + (falhas > 0 ? ' ' + formatar(textos.comFalhas, falhas) : '');
            conclusao.append(frase);

            const links = document.createElement('div');
            links.className = 'mt-2 d-flex flex-wrap gap-3';
            corpoFila.querySelectorAll('[data-grupo]').forEach(linha => {
                const colecao = linha.grupo.colecao;
                if (colecao) {
                    const link = document.createElement('a');
                    link.href = colecao.destino;
                    link.textContent = textos.abrirColecao + ': ' + colecao.nome;
                    links.append(link);
                }
            });
            const videos = document.createElement('a');
            videos.href = '/admin';
            videos.textContent = textos.verVideos;
            links.append(videos);
            conclusao.append(links);

            conclusao.classList.remove('d-none');
        }

        // Depois de escolhidos, os arquivos já estão na fila: o campo volta a ficar vazio, pronto
        // para escolher de novo.
        campoArquivos.addEventListener('change', function () {
            adicionarVideos(Array.from(campoArquivos.files).map(arquivo => ({ arquivo: arquivo, caminho: arquivo.name })));
            campoArquivos.value = '';
        });

        campoPasta.addEventListener('change', function () {
            const arquivos = Array.from(campoPasta.files);
            campoPasta.value = '';
            if (arquivos.length === 0) {
                return;
            }
            const entradas = arquivos.map(arquivo => ({ arquivo: arquivo, caminho: arquivo.webkitRelativePath || arquivo.name }));
            adicionarPasta(entradas[0].caminho.split('/')[0], entradas);
        });

        function escolher(qual) {
            (qual === 'pasta' ? campoPasta : campoArquivos).click();
        }

        area.addEventListener('click', function (evento) {
            const botaoEscolher = evento.target.closest('[data-escolher]');
            escolher(botaoEscolher ? botaoEscolher.dataset.escolher : 'arquivos');
        });

        area.addEventListener('keydown', function (evento) {
            if (evento.target === area && (evento.key === 'Enter' || evento.key === ' ')) {
                evento.preventDefault();
                escolher('arquivos');
            }
        });

        // Uma pasta sozinha vira coleção; qualquer outra combinação entra como vídeos soltos,
        // com as pastas lidas por inteiro.
        async function receber(entradas) {
            if (entradas.length === 0) {
                return;
            }

            try {
                if (entradas.length === 1 && entradas[0].isDirectory) {
                    adicionarPasta(entradas[0].name, await lerPasta(entradas[0]));
                    return;
                }

                const todas = [];
                for (const entrada of entradas) {
                    if (entrada.arquivoPronto) {
                        todas.push({ arquivo: entrada.arquivoPronto, caminho: entrada.arquivoPronto.name });
                    } else if (entrada.isDirectory) {
                        todas.push(...await lerPasta(entrada));
                    } else if (entrada.isFile) {
                        const arquivo = await new Promise((resolve, reject) => entrada.file(resolve, reject));
                        todas.push({ arquivo: arquivo, caminho: arquivo.name });
                    }
                }
                adicionarVideos(todas);
            } catch (falha) {
                mostrarErro(textos.falhaLeitura);
            }
        }

        soltarNaPagina = {
            ativo: () => formulario.isConnected,
            travada: () => false,
            destacar: function (sim) {
                area.classList.toggle('soltando', sim);
            },
            receber: receber
        };
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
