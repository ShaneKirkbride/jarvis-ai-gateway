#!/bin/sh
set -eu

mkdir -p /tmp/envoy/certs

printf "%s" "$CLIENT_CERT_PEM" > /tmp/envoy/certs/client.crt
printf "%s" "$CLIENT_KEY_PEM" > /tmp/envoy/certs/client.key
printf "%s" "$GATEWAY_SERVER_CA_PEM" > /tmp/envoy/certs/gateway-server-ca.crt

cat > /tmp/envoy/envoy.yaml <<EOF
static_resources:
  listeners:
  - name: openwebui_local_listener
    address:
      socket_address:
        address: 127.0.0.1
        port_value: 18080
    filter_chains:
    - filters:
      - name: envoy.filters.network.http_connection_manager
        typed_config:
          "@type": type.googleapis.com/envoy.extensions.filters.network.http_connection_manager.v3.HttpConnectionManager
          stat_prefix: openwebui_to_ai_gateway
          # Bound a stalled SSE stream without cutting a healthy one: the idle timer resets on
          # every chunk the gateway flushes, so this only fires if the model goes silent for
          # 5 minutes. Increase if very long quiet gaps between tokens are expected.
          stream_idle_timeout: 300s
          route_config:
            name: local_route
            virtual_hosts:
            - name: ai_gateway
              domains: ["*"]
              routes:
              - match:
                  prefix: "/"
                route:
                  cluster: ai_gateway_cluster
                  # SSE streaming (POST /v1/chat/completions with stream=true) holds the
                  # response open while tokens are produced. Envoy's default route timeout is
                  # 15s, which would sever a longer stream mid-flight. Disable the overall
                  # route timeout (0s) and rely on stream_idle_timeout instead — the AI gateway
                  # remains the authoritative timeout for non-streaming calls via
                  # Gateway:ProviderTimeoutSeconds.
                  timeout: 0s
request_headers_to_add:
- header:
    key: "X-Jarvis-Gateway-Key"
    value: "$GATEWAY_SERVICE_KEY"
  append_action: OVERWRITE_IF_EXISTS_OR_ADD
- header:
    key: "Authorization"
    value: "Bearer $GATEWAY_SERVICE_KEY"
  append_action: OVERWRITE_IF_EXISTS_OR_ADD
          http_filters:
          - name: envoy.filters.http.router
            typed_config:
              "@type": type.googleapis.com/envoy.extensions.filters.http.router.v3.Router

  clusters:
  - name: ai_gateway_cluster
    type: LOGICAL_DNS
    connect_timeout: 10s
    dns_lookup_family: V4_ONLY
    load_assignment:
      cluster_name: ai_gateway_cluster
      endpoints:
      - lb_endpoints:
        - endpoint:
            address:
              socket_address:
                address: $GATEWAY_HOSTNAME
                port_value: 443
    transport_socket:
      name: envoy.transport_sockets.tls
      typed_config:
        "@type": type.googleapis.com/envoy.extensions.transport_sockets.tls.v3.UpstreamTlsContext
        sni: $GATEWAY_SNI
        common_tls_context:
          tls_certificates:
          - certificate_chain:
              filename: /tmp/envoy/certs/client.crt
            private_key:
              filename: /tmp/envoy/certs/client.key
          validation_context:
            trusted_ca:
              filename: /tmp/envoy/certs/gateway-server-ca.crt

admin:
  address:
    socket_address:
      address: 127.0.0.1
      port_value: 9901
EOF

exec envoy -c /tmp/envoy/envoy.yaml --log-level info