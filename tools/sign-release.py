"""Signs release artifacts with the release key (ECDSA P-256 over SHA-256).

  sign-release.py --generate [--key PATH]    create the key pair once, print ONLY the public key (PEM)
  sign-release.py DIST_DIR [--key PATH]      write <asset>.sig for every *.exe and SHA256SUMS.txt in DIST_DIR

The private key lives in the gitignored .signing/release-key.pem next to the repo root and is never
printed, logged or copied anywhere. A signature file holds the base64 text of the raw 64 byte IEEE P1363
signature (r then s, 32 bytes each), the format the app's verifier reads.
"""
import argparse
import base64
import os
import sys

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.asymmetric.utils import decode_dss_signature

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_KEY = os.path.join(ROOT, ".signing", "release-key.pem")


def generate(key_path):
    if os.path.exists(key_path):
        print("A key already exists at that path; refusing to overwrite it.", file=sys.stderr)
        return 1

    os.makedirs(os.path.dirname(key_path), exist_ok=True)
    key = ec.generate_private_key(ec.SECP256R1())
    pem = key.private_bytes(
        serialization.Encoding.PEM,
        serialization.PrivateFormat.PKCS8,
        serialization.NoEncryption(),
    )
    # exclusive create: never replaces a key that appeared meanwhile
    with open(key_path, "xb") as handle:
        handle.write(pem)

    public = key.public_key().public_bytes(
        serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo
    )
    sys.stdout.write(public.decode("ascii"))
    return 0


def sign_file(key, path):
    with open(path, "rb") as handle:
        data = handle.read()

    r, s = decode_dss_signature(key.sign(data, ec.ECDSA(hashes.SHA256())))
    raw = r.to_bytes(32, "big") + s.to_bytes(32, "big")
    with open(path + ".sig", "w", encoding="ascii", newline="") as handle:
        handle.write(base64.b64encode(raw).decode("ascii") + "\n")


def sign_directory(dist, key_path):
    if not os.path.isfile(key_path):
        print("No signing key at the given path.", file=sys.stderr)
        return 1

    with open(key_path, "rb") as handle:
        key = serialization.load_pem_private_key(handle.read(), password=None)
    if not isinstance(key, ec.EllipticCurvePrivateKey) or key.curve.name != "secp256r1":
        print("The signing key is not an ECDSA P-256 key.", file=sys.stderr)
        return 1

    targets = sorted(
        name for name in os.listdir(dist)
        if name.lower().endswith(".exe") or name == "SHA256SUMS.txt"
    )
    if not targets:
        print("Nothing to sign in " + dist, file=sys.stderr)
        return 1

    for name in targets:
        sign_file(key, os.path.join(dist, name))
        print("signed " + name)
    return 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("dist", nargs="?")
    parser.add_argument("--generate", action="store_true")
    parser.add_argument("--key", default=DEFAULT_KEY)
    args = parser.parse_args()

    if args.generate:
        return generate(args.key)
    if not args.dist:
        parser.error("a dist folder is needed")
    return sign_directory(args.dist, args.key)


if __name__ == "__main__":
    sys.exit(main())
