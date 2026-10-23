# Ollama with the mapping model pulled on first start (kept on the /root/.ollama volume).
FROM ollama/ollama:0.9.0
ENV OLLAMA_HOST=[::]:11434 \
    OLLAMA_KEEP_ALIVE=30m \
    MODEL=llama3.2:3b
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
ENTRYPOINT ["/entrypoint.sh"]
