import subprocess, json, struct, threading, atexit


class ShellPowerPersistentWorker:
    def __init__(self, exe_path: str):
        self.p = subprocess.Popen(
            [exe_path],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            bufsize=0,  # unbuffered is helpful for pipes
        )
        # Optional: drain stderr in a background thread so it can't fill and stall
        self._stderr_lines = []
        self._t = threading.Thread(target=self._drain_stderr, daemon=True)
        self._t.start()
        atexit.register(self.close)

    def _drain_stderr(self):
        for line in self.p.stderr:
            self._stderr_lines.append(line.decode(errors="replace"))

    def call(self, req_obj: dict) -> dict:
        payload = json.dumps(req_obj).encode("utf-8")
        self.p.stdin.write(struct.pack("<I", len(payload)))
        self.p.stdin.write(payload)
        self.p.stdin.flush()

        (n,) = struct.unpack("<I", self._read_exact(4))
        data = self._read_exact(n)
        return json.loads(data.decode("utf-8"))

    def _read_exact(self, n: int) -> bytes:
        buf = b""
        while len(buf) < n:
            chunk = self.p.stdout.read(n - len(buf))
            if not chunk:
                raise RuntimeError("Worker terminated unexpectedly.\n" + "".join(self._stderr_lines[-50:]))
            buf += chunk
        return buf

    def close(self):
        try:
            self.p.kill()
        except Exception:
            pass
        try:
            self.p.wait(timeout=1)
        except Exception:
            pass

    def __del__(self):
        try:
            self.close()
        except Exception:
            pass