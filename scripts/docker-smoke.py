#!/usr/bin/env python3
"""実Docker上のPDF APIを検証する。Python標準ライブラリのみ使用する。"""

import contextlib
import http.client
import json
from pathlib import Path
import subprocess
import sys
import time
import uuid


FIXTURE = (Path(__file__).resolve().parent.parent / "tests/Amane.Pdf.Api.Tests/Fixtures/sample.pdf").read_bytes()
PASSWORD = "docker-fixture-日本語é🔒"
SENTINEL = "PDF-CONTENT-SENTINEL"
CHECK_ROOT = "/tmp/smoke-check"


def docker(*arguments, data=None, check=True):
    return subprocess.run(
        ["docker", *arguments], input=data, capture_output=True, check=check, timeout=45
    )


def multipart(file=None, password=PASSWORD):
    boundary = "smoke-" + uuid.uuid4().hex
    parts = []
    if file is not None:
        parts.append(
            (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="../../untrusted.pdf"\r\n'
             "Content-Type: application/octet-stream\r\n\r\n").encode() + file + b"\r\n"
        )
    if password is not None:
        parts.append(
            f'--{boundary}\r\nContent-Disposition: form-data; name="password"\r\n\r\n{password}\r\n'.encode()
        )
    parts.append(f"--{boundary}--\r\n".encode())
    return b"".join(parts), "multipart/form-data; boundary=" + boundary


class ApiContainer:
    def __init__(self, image, settings=(), isolated=False):
        self.name = "amane-pdf-smoke-" + uuid.uuid4().hex
        self.port = None
        self.image = image
        self.settings = settings
        self.isolated = isolated
        self.created = False

    def start(self):
        arguments = [
            "run", "--detach", "--name", self.name,
            "--read-only", "--tmpfs", "/tmp:rw,nosuid,nodev,noexec,size=256m",
            "--cpus", "1", "--memory", "512m", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges=true",
        ]
        if self.isolated:
            arguments += ["--network", "none"]
        else:
            arguments += ["--publish", "127.0.0.1::8080"]
        for setting in self.settings:
            arguments += ["--env", setting]
        docker(*arguments, self.image)
        self.created = True
        if not self.isolated:
            self.port = int(docker("port", self.name, "8080/tcp").stdout.decode().strip().rsplit(":", 1)[1])
        for _ in range(100):
            try:
                status, _, body = self.request("GET", "/healthz")
                if status == 200 and body == b"Healthy":
                    break
            except (OSError, subprocess.SubprocessError):
                pass
            time.sleep(0.2)
        else:
            raise AssertionError("Docker health check failed.")
        assert docker("exec", self.name, "id", "-u").stdout.strip() != b"0", "App must run as non-root."
        container = json.loads(docker("inspect", self.name).stdout)[0]
        assert container["HostConfig"]["ReadonlyRootfs"]
        assert "/tmp" in container["HostConfig"]["Tmpfs"]
        assert container["HostConfig"]["NanoCpus"] == 1_000_000_000
        assert container["HostConfig"]["Memory"] == 512 * 1024 * 1024
        assert not any(mount["Type"] == "volume" for mount in container["Mounts"])
        if self.isolated:
            assert container["HostConfig"]["NetworkMode"] == "none"

    def request(self, method, path, body=b"", content_type=None, chunked=False):
        if self.isolated:
            # コンテナ内loopbackだけでHTTPを送る。curl等をruntimeへ追加しない。
            headers = f"{method} {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\nContent-Length: {len(body)}\r\n"
            if content_type:
                headers += f"Content-Type: {content_type}\r\n"
            raw = docker(
                "exec", "--interactive", self.name, "bash", "-c",
                "exec 3<>/dev/tcp/127.0.0.1/8080; cat >&3; cat <&3",
                data=(headers + "\r\n").encode() + body,
            ).stdout
            if b"\r\n\r\n" not in raw:
                raise ConnectionError("Container HTTP server is not ready.")
            head, response_body = raw.split(b"\r\n\r\n", 1)
            lines = head.decode().split("\r\n")
            return int(lines[0].split()[1]), dict(line.split(": ", 1) for line in lines[1:]), response_body
        headers = {}
        if content_type:
            headers["Content-Type"] = content_type
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=40)
        try:
            payload = (body[i:i + 4096] for i in range(0, len(body), 4096)) if chunked else body
            connection.request(method, path, body=payload, headers=headers, encode_chunked=chunked)
            response = connection.getresponse()
            return response.status, dict(response.getheaders()), response.read()
        finally:
            connection.close()

    def protect(self, file, password=PASSWORD, expected=200, chunked=False):
        body, content_type = multipart(file, password)
        status, headers, result = self.request("POST", "/api/pdf/protect", body, content_type, chunked)
        assert status == expected, f"Expected HTTP {expected}, received {status}."
        media_type = headers.get("Content-Type", "").split(";", 1)[0]
        if expected == 200:
            assert media_type == "application/pdf" and result.startswith(b"%PDF-")
        else:
            assert media_type == "application/problem+json"
            assert json.loads(result)["status"] == expected
            for secret in (PASSWORD.encode(), SENTINEL.encode(), b"/tmp/", b"stack", b"qpdf"):
                assert secret not in result, "Problem Details exposed internal information."
        self.assert_clean()
        return result

    def rotate(self, file, angle=90, pages=None):
        body, content_type = multipart(file, password=None)
        path = f"/api/pdf/rotate?angle={angle}"
        if pages is not None:
            path += "&pages=" + pages
        status, headers, result = self.request("POST", path, body, content_type)
        assert status == 200, f"Expected HTTP 200, received {status}."
        assert headers.get("Content-Type", "").split(";", 1)[0] == "application/pdf"
        assert "filename=rotated.pdf" in headers.get("Content-Disposition", "")
        assert result.startswith(b"%PDF-")
        self.assert_clean()
        return result

    def extract(self, file, pages):
        body, content_type = multipart(file, password=None)
        status, headers, result = self.request("POST", "/api/pdf/extract?pages=" + pages, body, content_type)
        assert status == 200, f"Expected HTTP 200, received {status}."
        assert headers.get("Content-Type", "").split(";", 1)[0] == "application/pdf"
        assert "filename=extracted.pdf" in headers.get("Content-Disposition", "")
        assert result.startswith(b"%PDF-")
        self.assert_clean()
        return result

    def assert_clean(self):
        assert docker(
            "exec", self.name, "sh", "-c",
            "if [ -d /tmp/amane-pdf-api ]; then find /tmp/amane-pdf-api -mindepth 1 -print; fi",
        ).stdout == b"", "PDF temporary files remain."
        result = docker("logs", self.name)
        logs = result.stdout + result.stderr
        for secret in (PASSWORD.encode(), SENTINEL.encode(), b"../../untrusted.pdf", b"/tmp/amane-pdf-api/"):
            assert secret not in logs, "Application logs exposed input or job paths."

    def verify_encryption(self, pdf):
        docker("exec", self.name, "mkdir", "-m", "700", "-p", CHECK_ROOT)
        docker(
            "exec", "--interactive", self.name, "sh", "-c",
            "umask 077; cat > /tmp/smoke-check/output.pdf", data=pdf,
        )
        output = CHECK_ROOT + "/output.pdf"
        assert docker("exec", self.name, "qpdf", "--is-encrypted", output, check=False).returncode == 0
        for password, inspection, exit_code in ((PASSWORD, "check", 0), (PASSWORD, "showEncryption", 0), ("incorrect-fixture-password", "check", 2)):
            job = json.dumps({"inputFile": output, "password": password, inspection: ""}).encode()
            docker(
                "exec", "--interactive", self.name, "sh", "-c",
                "umask 077; cat > /tmp/smoke-check/job.json", data=job,
            )
            result = docker("exec", self.name, "qpdf", "--job-json-file=" + CHECK_ROOT + "/job.json", check=False)
            assert result.returncode == exit_code, "Password/inspection check failed."
            if inspection == "showEncryption":
                assert b"AESv3" in result.stdout and b"R = 6" in result.stdout
        docker("exec", self.name, "rm", "-rf", CHECK_ROOT)

    def page_rotations(self, pdf):
        docker("exec", self.name, "mkdir", "-m", "700", "-p", CHECK_ROOT)
        docker(
            "exec", "--interactive", self.name, "sh", "-c",
            "umask 077; cat > /tmp/smoke-check/output.pdf", data=pdf,
        )
        result = docker("exec", self.name, "qpdf", "--json", CHECK_ROOT + "/output.pdf")
        document = json.loads(result.stdout)
        objects = document["qpdf"][1]
        rotations = [
            objects["obj:" + page["object"]]["value"].get("/Rotate", 0)
            for page in document["pages"]
        ]
        docker("exec", self.name, "rm", "-rf", CHECK_ROOT)
        return rotations

    def close(self):
        if self.created:
            docker("rm", "--force", self.name)


@contextlib.contextmanager
def running_container(image, settings=(), isolated=False):
    api = ApiContainer(image, settings, isolated)
    try:
        api.start()
        yield api
    finally:
        api.close()


def main(image):
    with running_container(image) as api:
        print(docker("exec", api.name, "qpdf", "--version").stdout.decode().strip())
        schema = json.loads(docker("exec", api.name, "qpdf", "--job-json-help").stdout)
        assert "256bit" in schema["encrypt"] and "passwordMode" in schema and "check" in schema and "isEncrypted" in schema
        encrypted = api.protect(FIXTURE)
        api.verify_encryption(encrypted)
        rotated = api.rotate(FIXTURE)
        assert api.page_rotations(rotated) == [90]
        assert api.page_rotations(api.rotate(rotated, pages="1")) == [180]
        extracted = api.extract(FIXTURE, "1")
        assert api.page_rotations(extracted) == [0]
        api.protect(None, expected=400)
        api.protect(FIXTURE, password=None, expected=400)
        api.protect(b"", expected=422)
        api.protect(SENTINEL.encode(), expected=422)
        api.protect(b"%PDF-1.4\n" + SENTINEL.encode() + b"\n%%EOF", expected=422)
        api.protect(FIXTURE.replace(b"/Length 41", b"/Length 39"), expected=422)
        api.protect(encrypted, expected=422)
        print("Docker E2E: health/non-root/read-only/tmpfs/AES-256/rotation/page selection/passwords/400/422/logs/cleanup PASS")
    with running_container(image, (f"Pdf__MaxFileBytes={len(FIXTURE)}",)) as api:
        api.protect(FIXTURE)
        api.protect(FIXTURE + b"X", expected=413)
        api.protect(FIXTURE + b"X", expected=413, chunked=True)
        print("Docker E2E: exact file limit / Content-Length and chunked 413 PASS")
    with running_container(image, ("Pdf__QpdfPath=/missing/qpdf",)) as api:
        api.protect(FIXTURE, expected=500)
        print("Docker E2E: sanitized internal failure / cleanup PASS")
    with running_container(image, isolated=True) as api:
        api.verify_encryption(api.protect(FIXTURE))
        print("Docker E2E: no network / loopback HTTP / real encryption PASS")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "amane-pdf-api:ci")
