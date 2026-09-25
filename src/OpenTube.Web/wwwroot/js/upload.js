// Envio do arquivo direto do navegador para o storage, em pedaços assinados pela aplicação.
// O servidor nunca recebe os bytes do vídeo: só coordena o envio.
(function () {
    const LOTE_DE_ASSINATURAS = 50;

    function selecionar(seletor) {
        return document.querySelector(seletor);
    }

    async function postar(url, corpo, token) {
        const resposta = await fetch(url, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': token
            },
            body: JSON.stringify(corpo)
        });

        if (!resposta.ok) {
            let mensagem = 'Falha na comunicação com o servidor.';
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
            throw new Error('Falha ao enviar um pedaço do arquivo.');
        }

        const etag = resposta.headers.get('ETag');
        if (!etag) {
            throw new Error('O storage não confirmou o recebimento do pedaço.');
        }

        return etag;
    }

    function iniciar() {
        const formulario = selecionar('#formulario-envio');
        if (!formulario) {
            return;
        }

        const token = formulario.dataset.token;
        const barra = selecionar('#barra-progresso');
        const situacao = selecionar('#situacao-envio');
        const erro = selecionar('#erro-envio');
        const botao = selecionar('#botao-enviar');

        function progresso(percentual, texto) {
            barra.parentElement.classList.remove('d-none');
            barra.style.width = percentual + '%';
            barra.setAttribute('aria-valuenow', String(percentual));
            situacao.textContent = texto;
        }

        formulario.addEventListener('submit', async function (evento) {
            evento.preventDefault();

            const arquivo = selecionar('#arquivo').files[0];
            if (!arquivo) {
                return;
            }

            erro.classList.add('d-none');
            botao.disabled = true;

            let bilhete = null;

            try {
                progresso(0, 'Preparando o envio…');

                bilhete = await postar('/api/admin/envios/iniciar', {
                    titulo: selecionar('#titulo').value,
                    descricao: selecionar('#descricao').value,
                    arquivo: arquivo.name,
                    tipo: arquivo.type,
                    tamanho: arquivo.size
                }, token);

                const enviados = [];
                let assinadas = bilhete.partes;

                for (let numero = 1; numero <= bilhete.totalDePedacos; numero++) {
                    if (!assinadas.some(p => p.numero === numero)) {
                        const lote = await postar(`/api/admin/envios/${bilhete.videoId}/partes`, {
                            uploadId: bilhete.uploadId,
                            primeira: numero,
                            quantidade: Math.min(LOTE_DE_ASSINATURAS, bilhete.totalDePedacos - numero + 1)
                        }, token);

                        assinadas = lote.partes;
                    }

                    const parte = assinadas.find(p => p.numero === numero);
                    const inicio = (numero - 1) * bilhete.tamanhoDoPedaco;
                    const fim = Math.min(inicio + bilhete.tamanhoDoPedaco, arquivo.size);

                    const etag = await enviarPedaco(parte.url, arquivo.slice(inicio, fim));
                    enviados.push({ numero: numero, eTag: etag });

                    progresso(
                        Math.round((numero / bilhete.totalDePedacos) * 100),
                        `Enviando… ${numero} de ${bilhete.totalDePedacos} pedaços`);
                }

                progresso(100, 'Finalizando…');

                const conclusao = await postar(`/api/admin/envios/${bilhete.videoId}/concluir`, {
                    uploadId: bilhete.uploadId,
                    partes: enviados
                }, token);

                window.location.href = conclusao.destino;
            } catch (falha) {
                erro.textContent = falha.message;
                erro.classList.remove('d-none');
                situacao.textContent = '';
                botao.disabled = false;

                if (bilhete) {
                    // Libera os pedaços já recebidos em vez de deixá-los ocupando espaço.
                    try {
                        await postar(`/api/admin/envios/${bilhete.videoId}/cancelar`,
                            { uploadId: bilhete.uploadId }, token);
                    } catch (_) {
                        // Nada a fazer: o storage descarta envios incompletos por conta própria.
                    }
                }
            }
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', iniciar);
    } else {
        iniciar();
    }
})();
