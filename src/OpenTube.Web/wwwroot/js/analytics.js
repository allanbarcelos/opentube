// Registro do que foi assistido. Relata trechos, e não porcentagem: é o que permite saber
// se a pessoa pulou um pedaço e onde o público abandona o vídeo.
window.openTubeAnalytics = (function () {
    const INTERVALO_DE_ENVIO = 10000;

    function criar(videoId, elemento) {
        let sessaoId = null;
        let pendentes = [];
        let inicioDoTrecho = null;
        let ultimaPosicao = 0;
        let temporizador = null;
        let encerrada = false;

        async function iniciar() {
            try {
                const resposta = await fetch('/api/playback/start', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ videoId: videoId })
                });

                if (!resposta.ok) {
                    return;
                }

                sessaoId = (await resposta.json()).sessaoId;
                temporizador = setInterval(enviar, INTERVALO_DE_ENVIO);
            } catch (_) {
                // Sem registro de audiência o vídeo continua tocando: a reprodução nunca
                // pode depender da coleta.
            }
        }

        function relatar(tipo, em, de, ate, detalhe) {
            pendentes.push({
                tipo: tipo,
                em: Number(em) || 0,
                de: de === undefined ? null : de,
                ate: ate === undefined ? null : ate,
                detalhe: detalhe === undefined ? null : detalhe
            });
        }

        function fecharTrecho() {
            if (inicioDoTrecho === null) {
                return;
            }

            const fim = ultimaPosicao;

            if (fim - inicioDoTrecho > 0.25) {
                relatar('progress', fim, inicioDoTrecho, fim);
            }

            inicioDoTrecho = null;
        }

        function enviar(usarBeacon) {
            if (!sessaoId || pendentes.length === 0) {
                return;
            }

            const corpo = JSON.stringify({ eventos: pendentes });
            pendentes = [];

            const endereco = `/api/playback/${sessaoId}/events`;

            if (usarBeacon && navigator.sendBeacon) {
                // A aba pode estar fechando: sendBeacon é o único envio que sobrevive a isso.
                navigator.sendBeacon(endereco, new Blob([corpo], { type: 'application/json' }));
                return;
            }

            fetch(endereco, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: corpo,
                keepalive: true
            }).catch(function () { });
        }

        function encerrar() {
            if (encerrada) {
                return;
            }

            encerrada = true;
            fecharTrecho();
            enviar(true);

            if (temporizador) {
                clearInterval(temporizador);
                temporizador = null;
            }

            if (sessaoId && navigator.sendBeacon) {
                navigator.sendBeacon(`/api/playback/${sessaoId}/close`, new Blob([''], { type: 'application/json' }));
            }
        }

        elemento.addEventListener('play', function () {
            inicioDoTrecho = elemento.currentTime;
            ultimaPosicao = elemento.currentTime;
        });

        elemento.addEventListener('timeupdate', function () {
            if (!elemento.seeking) {
                ultimaPosicao = elemento.currentTime;
            }
        });

        elemento.addEventListener('pause', function () {
            fecharTrecho();
            relatar('pause', elemento.currentTime);
        });

        elemento.addEventListener('seeking', function () {
            // Fecha o trecho na última posição conhecida: a posição atual já é o destino
            // do salto, e usá-la contaria como assistido um pedaço que ninguém viu.
            fecharTrecho();
        });

        elemento.addEventListener('seeked', function () {
            relatar('seek', elemento.currentTime);
            ultimaPosicao = elemento.currentTime;
            inicioDoTrecho = elemento.paused ? null : elemento.currentTime;
        });

        elemento.addEventListener('ended', function () {
            ultimaPosicao = elemento.duration || elemento.currentTime;
            fecharTrecho();
            relatar('ended', ultimaPosicao);
            enviar(false);
        });

        elemento.addEventListener('error', function () {
            relatar('error', elemento.currentTime, undefined, undefined, 'falha na reprodução');
        });

        window.addEventListener('pagehide', encerrar);
        document.addEventListener('visibilitychange', function () {
            if (document.visibilityState === 'hidden') {
                fecharTrecho();
                enviar(true);
            }
        });

        iniciar();

        return {
            qualidade: function (nome) {
                relatar('quality', elemento.currentTime, undefined, undefined, nome);
            },
            erro: function (descricao) {
                relatar('error', elemento.currentTime, undefined, undefined, descricao);
            },
            encerrar: encerrar
        };
    }

    return { criar: criar };
})();
