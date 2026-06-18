FROM envoyproxy/envoy:v1.31.5

USER root

COPY docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh

EXPOSE 18080

ENTRYPOINT ["/docker-entrypoint.sh"]
