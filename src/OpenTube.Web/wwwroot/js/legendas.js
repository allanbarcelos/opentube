// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

// Aba de legendas: enquanto alguma legenda estiver processando, consulta a situação de tempos em
// tempos e recarrega a página quando ela muda — a transcrição roda em segundo plano no worker.
// Carregado no layout e montado sozinho, como o player.
window.openTubeLegendas = (function () {
    const INTERVALO = 4000;
    let temporizador = null;

    function montar() {
        clearInterval(temporizador);
        temporizador = null;

        const painel = document.querySelector('[data-legendas-status]');
        if (!painel || painel.dataset.legendasProcessando !== 'true') {
            return;
        }

        const inicial = new Map(Array.from(painel.querySelectorAll('[data-legenda]'))
            .map(linha => [linha.dataset.legenda, linha.dataset.status]));

        temporizador = setInterval(async function () {
            // A navegação tirou o painel da página: a consulta para junto.
            if (!painel.isConnected) {
                clearInterval(temporizador);
                return;
            }

            try {
                const resposta = await fetch(painel.dataset.legendasStatus, { headers: { Accept: 'application/json' } });
                if (!resposta.ok) {
                    return;
                }

                const lista = await resposta.json();
                const mudou = lista.length !== inicial.size
                    || lista.some(item => inicial.get(item.id) !== item.status);

                if (mudou) {
                    clearInterval(temporizador);
                    location.reload();
                }
            } catch (_) {
                // Sem conexão agora; a próxima consulta tenta de novo.
            }
        }, INTERVALO);
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
