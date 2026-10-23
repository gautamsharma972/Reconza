#!/bin/sh
set -e
ollama serve &
pid=$!
until ollama list >/dev/null 2>&1; do sleep 1; done
ollama list | grep -q "^${MODEL}" || ollama pull "$MODEL"
wait $pid
