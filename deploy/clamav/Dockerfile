# ClamAV daemon on 3310, listening on IPv6 + IPv4 for Railway private networking. Signatures update via freshclam.
FROM clamav/clamav:1.4
RUN echo "TCPAddr ::" >> /etc/clamav/clamd.conf && \
    sed -i 's/^#\?StreamMaxLength.*/StreamMaxLength 60M/' /etc/clamav/clamd.conf && \
    grep -q '^StreamMaxLength' /etc/clamav/clamd.conf || echo "StreamMaxLength 60M" >> /etc/clamav/clamd.conf
EXPOSE 3310
