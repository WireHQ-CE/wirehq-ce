package agent

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"time"
)

// The client certificate WireHQ issues is short-lived (45 days). The agent renews it once two-thirds of its
// lifetime has passed (~15 days of headroom on a 45-day leaf), so a host can be offline for two weeks and still
// come back on its own. A failed attempt is retried after rotateRetryBackoff rather than every poll.
const (
	renewAfterFraction = 2.0 / 3.0
	rotateRetryBackoff = 15 * time.Minute
)

type rotateRequest struct {
	Csr string `json:"csr"`
}

type rotateResponse struct {
	CertificatePEM   string `json:"certificatePem"`
	CaCertificatePEM string `json:"caCertificatePem"`
}

// rotationDue reports whether a certificate has used up enough of its lifetime to be renewed.
func rotationDue(leaf *x509.Certificate, now time.Time) bool {
	lifetime := leaf.NotAfter.Sub(leaf.NotBefore)
	renewAt := leaf.NotBefore.Add(time.Duration(float64(lifetime) * renewAfterFraction))
	return !now.Before(renewAt)
}

// rotateCertificate re-keys the agent: a fresh EC P-256 key + CSR, signed by the gateway while authenticated with
// the current certificate. The new identity is persisted crash-safely BEFORE the client switches to it: the
// gateway keeps accepting the old cert until the agent first heartbeats on the new one, so as long as the new
// cert is not used until it is on disk, there is no point at which a crash or a lost response can lock the agent
// out. Returns the new leaf and the CA PEM the gateway returned.
func rotateCertificate(ctx context.Context, cfg Config, client *Client) (*x509.Certificate, []byte, error) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return nil, nil, fmt.Errorf("generate key: %w", err)
	}
	csrDER, err := x509.CreateCertificateRequest(rand.Reader,
		&x509.CertificateRequest{Subject: pkix.Name{CommonName: "wirehq-agent"}}, key)
	if err != nil {
		return nil, nil, fmt.Errorf("create csr: %w", err)
	}
	keyDER, err := x509.MarshalPKCS8PrivateKey(key)
	if err != nil {
		return nil, nil, err
	}
	keyPEM := pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: keyDER})
	csrPEM := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE REQUEST", Bytes: csrDER})

	var issued rotateResponse
	if err := client.do(ctx, "POST", "/agent/v1/cert/rotate", rotateRequest{Csr: string(csrPEM)}, &issued); err != nil {
		return nil, nil, fmt.Errorf("rotate request: %w", err)
	}

	// The issued cert must be for OUR key — never persist a pair that would not load.
	pair, err := tls.X509KeyPair([]byte(issued.CertificatePEM), keyPEM)
	if err != nil {
		return nil, nil, fmt.Errorf("issued certificate does not match the new key: %w", err)
	}
	leaf, err := x509.ParseCertificate(pair.Certificate[0])
	if err != nil {
		return nil, nil, fmt.Errorf("parse issued certificate: %w", err)
	}

	if err := persistIdentity(cfg, keyPEM, []byte(issued.CertificatePEM)); err != nil {
		// Do NOT switch to the new cert: using it would end the gateway's grace for the old one, which is the
		// only cert on disk — the next restart would be locked out. Staying on the old cert, we simply retry.
		return nil, nil, fmt.Errorf("persist rotated identity: %w", err)
	}

	client.useCertificate(pair)
	return leaf, []byte(issued.CaCertificatePEM), nil
}

func nextPath(path string) string { return path + ".next" }

// persistIdentity writes the new key + cert next to the live ones, then promotes them by rename (key first, cert
// second). Each rename is atomic; recoverPendingRotation finishes the job if the process dies between them.
func persistIdentity(cfg Config, keyPEM, certPEM []byte) error {
	if err := writeFileSynced(nextPath(cfg.keyPath()), keyPEM, 0o600); err != nil {
		return err
	}
	if err := writeFileSynced(nextPath(cfg.certPath()), certPEM, 0o600); err != nil {
		return err
	}
	if err := os.Rename(nextPath(cfg.keyPath()), cfg.keyPath()); err != nil {
		return err
	}
	if err := os.Rename(nextPath(cfg.certPath()), cfg.certPath()); err != nil {
		return err
	}
	syncDir(cfg.StateDir)
	return nil
}

// recoverPendingRotation completes (or discards) a rotation the previous process did not finish persisting, so
// the identity on disk is always a loadable key + cert pair. Runs before the client loads its certificate.
//
//   - both .next files form a pair → crash before either rename: promote both (the gateway issued that cert as
//     current, and still accepts the old one, so either would work — the new one is simply fresher);
//   - only cert.next remains and pairs with the live key → crash between the renames: promote the cert;
//   - anything else (a torn write) → discard the leftovers; the live pair is untouched and still accepted.
func recoverPendingRotation(cfg Config) error {
	keyNext, certNext := nextPath(cfg.keyPath()), nextPath(cfg.certPath())
	_, keyErr := os.Stat(keyNext)
	_, certErr := os.Stat(certNext)
	if errors.Is(keyErr, os.ErrNotExist) && errors.Is(certErr, os.ErrNotExist) {
		return nil
	}

	switch {
	case keyErr == nil && certErr == nil && loadsAsPair(certNext, keyNext):
		if err := os.Rename(keyNext, cfg.keyPath()); err != nil {
			return err
		}
		if err := os.Rename(certNext, cfg.certPath()); err != nil {
			return err
		}
	case errors.Is(keyErr, os.ErrNotExist) && certErr == nil && loadsAsPair(certNext, cfg.keyPath()):
		if err := os.Rename(certNext, cfg.certPath()); err != nil {
			return err
		}
	default:
		_ = os.Remove(keyNext)
		_ = os.Remove(certNext)
	}
	syncDir(cfg.StateDir)
	return nil
}

func loadsAsPair(certPath, keyPath string) bool {
	_, err := tls.LoadX509KeyPair(certPath, keyPath)
	return err == nil
}

// writeFileAtomic replaces path via a synced temp file + rename, so a reader never sees a partial file.
func writeFileAtomic(path string, data []byte, perm os.FileMode) error {
	tmp := path + ".tmp"
	if err := writeFileSynced(tmp, data, perm); err != nil {
		return err
	}
	if err := os.Rename(tmp, path); err != nil {
		return err
	}
	syncDir(filepath.Dir(path))
	return nil
}

func writeFileSynced(path string, data []byte, perm os.FileMode) error {
	f, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_TRUNC, perm)
	if err != nil {
		return err
	}
	if _, err := f.Write(data); err != nil {
		_ = f.Close()
		return err
	}
	if err := f.Sync(); err != nil {
		_ = f.Close()
		return err
	}
	return f.Close()
}

// syncDir makes completed renames durable. Best-effort: not every platform supports syncing a directory.
func syncDir(dir string) {
	if d, err := os.Open(dir); err == nil {
		_ = d.Sync()
		_ = d.Close()
	}
}
