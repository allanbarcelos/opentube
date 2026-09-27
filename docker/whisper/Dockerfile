# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

# Servidor do Whisper (whisper.cpp) para as legendas automáticas. Duas variantes:
#
#   cpu   imagem leve; traz o código de CPU compilado para várias gerações de processador
#         (SSE, AVX, AVX2, AVX-512 / variantes ARM) e escolhe a melhor ao iniciar
#   cuda  para servidores com GPU NVIDIA; sem GPU visível, cai para a CPU sozinha
#
#   docker build -f docker/whisper/Dockerfile --target cpu  -t opentube-whisper:cpu  .
#   docker build -f docker/whisper/Dockerfile --target cuda -t opentube-whisper:cuda .
#
# O modelo não vai na imagem: o entrypoint escolhe um conforme o hardware e o baixa no volume
# /models na primeira vez.

ARG WHISPER_VERSION=v1.9.4
ARG CUDA_VERSION=12.6.3
ARG UBUNTU_VERSION=24.04

# Opções comuns: sem otimizar para a máquina do build (a imagem roda em outras), com os
# backends carregados em tempo de execução, e só o servidor.
ARG CMAKE_COMUM="-DCMAKE_BUILD_TYPE=Release -DGGML_NATIVE=OFF -DGGML_BACKEND_DL=ON -DGGML_CPU_ALL_VARIANTS=ON -DWHISPER_BUILD_TESTS=OFF -DWHISPER_BUILD_EXAMPLES=ON -DWHISPER_BUILD_SERVER=ON -DWHISPER_SDL2=OFF -DWHISPER_CURL=OFF"

# ── Código ────────────────────────────────────────────────────────────────────
FROM ubuntu:${UBUNTU_VERSION} AS fonte
ARG WHISPER_VERSION
RUN apt-get update \
    && apt-get install --no-install-recommends -y git ca-certificates \
    && rm -rf /var/lib/apt/lists/*
RUN git clone --depth 1 --branch "${WHISPER_VERSION}" https://github.com/ggml-org/whisper.cpp /src

# ── Compilação: CPU ───────────────────────────────────────────────────────────
FROM ubuntu:${UBUNTU_VERSION} AS build-cpu
ARG CMAKE_COMUM
# GCC 14: o 13 do Ubuntu não compila as variantes ARMv9 mais novas (SME).
RUN apt-get update \
    && apt-get install --no-install-recommends -y build-essential gcc-14 g++-14 cmake \
    && rm -rf /var/lib/apt/lists/*
COPY --from=fonte /src /src
WORKDIR /src
RUN CC=gcc-14 CXX=g++-14 cmake -B build ${CMAKE_COMUM} \
    && cmake --build build -j"$(nproc)" --target whisper-server \
    && mkdir -p /out \
    && cp build/bin/whisper-server /out/ \
    && find build -name '*.so*' -exec cp -P {} /out/ \;

# ── Execução: CPU ─────────────────────────────────────────────────────────────
# Antes dos estágios de CUDA: um build sem BuildKit para no alvo pedido e não compila o resto.
FROM ubuntu:${UBUNTU_VERSION} AS cpu
RUN apt-get update \
    && apt-get install --no-install-recommends -y ca-certificates curl libgomp1 \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 1001 --create-home whisper \
    && mkdir -p /models && chown 1001 /models
COPY --from=build-cpu /out/ /opt/whisper/bin/
COPY docker/whisper/modelos.txt /opt/whisper/modelos.txt
COPY docker/whisper/entrypoint.sh /opt/whisper/entrypoint.sh
RUN chmod 755 /opt/whisper/entrypoint.sh
# Os backends (libggml-cpu-*.so) são procurados ao lado do executável.
ENV PATH="/opt/whisper/bin:${PATH}" \
    LD_LIBRARY_PATH="/opt/whisper/bin"
USER 1001
VOLUME /models
EXPOSE 8080
# O primeiro início baixa o modelo; até lá o serviço ainda não está pronto, e não doente.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30m --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/health || exit 1
ENTRYPOINT ["/opt/whisper/entrypoint.sh"]


# ── Compilação: CUDA ──────────────────────────────────────────────────────────
FROM nvidia/cuda:${CUDA_VERSION}-devel-ubuntu${UBUNTU_VERSION} AS build-cuda
ARG CMAKE_COMUM
# Pascal (GTX 10xx) até Hopper (H100), incluindo as T4, A10, L4 e RTX dos provedores de nuvem.
ARG CUDA_ARCHITECTURES="61;70;75;80;86;89;90"
RUN apt-get update \
    && apt-get install --no-install-recommends -y build-essential cmake \
    && rm -rf /var/lib/apt/lists/*
COPY --from=fonte /src /src
WORKDIR /src
RUN cmake -B build ${CMAKE_COMUM} -DGGML_CUDA=ON -DCMAKE_CUDA_ARCHITECTURES="${CUDA_ARCHITECTURES}" \
    && cmake --build build -j"$(nproc)" --target whisper-server \
    && mkdir -p /out \
    && cp build/bin/whisper-server /out/ \
    && find build -name '*.so*' -exec cp -P {} /out/ \;

# ── Execução: CUDA ────────────────────────────────────────────────────────────
FROM nvidia/cuda:${CUDA_VERSION}-runtime-ubuntu${UBUNTU_VERSION} AS cuda
RUN apt-get update \
    && apt-get install --no-install-recommends -y ca-certificates curl libgomp1 \
    && rm -rf /var/lib/apt/lists/* \
    && (userdel -r ubuntu 2>/dev/null || true) \
    && useradd --uid 1001 --create-home whisper \
    && mkdir -p /models && chown 1001 /models
COPY --from=build-cuda /out/ /opt/whisper/bin/
COPY docker/whisper/modelos.txt /opt/whisper/modelos.txt
COPY docker/whisper/entrypoint.sh /opt/whisper/entrypoint.sh
RUN chmod 755 /opt/whisper/entrypoint.sh
ENV PATH="/opt/whisper/bin:${PATH}" \
    LD_LIBRARY_PATH="/opt/whisper/bin" \
    WHISPER_CUDA=1 \
    NVIDIA_VISIBLE_DEVICES=all \
    NVIDIA_DRIVER_CAPABILITIES=compute,utility
USER 1001
VOLUME /models
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30m --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/health || exit 1
ENTRYPOINT ["/opt/whisper/entrypoint.sh"]
