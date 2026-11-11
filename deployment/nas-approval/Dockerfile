FROM python:3.13-slim
WORKDIR /app
COPY server.py /app/server.py
RUN chmod 0644 /app/server.py
ENV PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1
USER 1000:1000
EXPOSE 8743
CMD ["python", "/app/server.py"]
