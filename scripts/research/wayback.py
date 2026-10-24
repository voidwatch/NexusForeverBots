"""Polite Wayback Machine client: one persistent HTTPS connection, at most one request per second, backoff when refused."""
import http.client, time, urllib.parse

HOST = 'web.archive.org'
UA = 'NexusForeverBots-research/1.0 (personal emulator QA, 1 request per second)'


class Wayback:
    def __init__(self, min_interval=1.0):
        self.conn = None
        self.last = 0.0
        self.min_interval = min_interval
        self.failures = 0

    def _connect(self):
        if self.conn:
            try:
                self.conn.close()
            except Exception:
                pass
        self.conn = http.client.HTTPSConnection(HOST, timeout=90)

    def get(self, ts, url, tries=6):
        """Raw archived page (id_ flavour) or None when the archive has none (404/403)."""
        path = f'/web/{ts}id_/{url}'
        for attempt in range(tries):
            wait = self.last + self.min_interval - time.time()
            if wait > 0:
                time.sleep(wait)
            self.last = time.time()
            try:
                if self.conn is None:
                    self._connect()
                for _ in range(4):  # follow redirects to the closest snapshot
                    self.conn.request('GET', path, headers={'User-Agent': UA, 'Accept-Encoding': 'identity'})
                    r = self.conn.getresponse()
                    body = r.read()
                    if r.status in (301, 302, 303, 307, 308):
                        loc = r.getheader('Location') or ''
                        u = urllib.parse.urlparse(loc)
                        if u.netloc and u.netloc != HOST:
                            return None
                        path = u.path + (('?' + u.query) if u.query else '')
                        continue
                    break
                if r.status == 200:
                    self.failures = 0
                    return body
                if r.status in (404, 403):
                    return None
                raise RuntimeError(f'status {r.status}')
            except Exception:
                self.failures += 1
                self.conn = None
                time.sleep(min(300, 20 * (attempt + 1)))
        return None
