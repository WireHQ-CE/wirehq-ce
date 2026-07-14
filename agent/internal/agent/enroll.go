package agent

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/json"
	"encoding/pem"
	"fmt"
	"io"
	"net/http"
	"os"
	"runtime"
	"time"
)

// Enroll redeems a single-use token: it generates a fresh EC P-256 key + CSR, posts them to the gateway,
// and stores the issued client certificate, the org CA certificate, and the agent id. The private key never
// leaves the host. Idempotent only in the sense that a spent token cannot be reused — re-enrolling needs a
// new token. (ADR-028)
func Enroll(ctx context.Context, cfg Config, token, name string) (string, error) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return "", fmt.Errorf("generate key: %w", err)
	}

	csrDER, err := x509.CreateCertificateRequest(rand.Reader,
		&x509.CertificateRequest{Subject: pkix.Name{CommonName: "wirehq-agent"}}, key)
	if err != nil {
		return "", fmt.Errorf("create csr: %w", err)
	}
	csrPEM := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE REQUEST", Bytes: csrDER})

	reqBody, err := json.Marshal(enrollRequest{
		Token:    token,
		Csr:      string(csrPEM),
		Name:     name,
		Platform: runtime.GOOS + "-" + runtime.GOARCH,
	})
	if err != nil {
		return "", err
	}

	client := &http.Client{
		Timeout: 30 * time.Second,
		Transport: &http.Transport{
			TLSClientConfig: &tls.Config{InsecureSkipVerify: cfg.InsecureSkipVerify}, //nolint:gosec // dev-only flag
		},
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, cfg.Server+"/agent/v1/enroll", bytes.NewReader(reqBody))
	if err != nil {
		return "", err
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := client.Do(req)
	if err != nil {
		return "", fmt.Errorf("enroll request: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		snippet, _ := io.ReadAll(io.LimitReader(resp.Body, 512))
		return "", fmt.Errorf("enroll rejected: %s: %s", resp.Status, string(snippet))
	}

	var issued enrollResponse
	if err := json.NewDecoder(resp.Body).Decode(&issued); err != nil {
		return "", fmt.Errorf("decode enroll response: %w", err)
	}

	keyDER, err := x509.MarshalPKCS8PrivateKey(key)
	if err != nil {
		return "", err
	}
	keyPEM := pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: keyDER})

	if err := os.MkdirAll(cfg.StateDir, 0o700); err != nil {
		return "", fmt.Errorf("create state dir: %w", err)
	}
	if err := os.WriteFile(cfg.keyPath(), keyPEM, 0o600); err != nil {
		return "", err
	}
	if err := os.WriteFile(cfg.certPath(), []byte(issued.CertificatePEM), 0o600); err != nil {
		return "", err
	}
	if err := os.WriteFile(cfg.caPath(), []byte(issued.CaCertificatePEM), 0o644); err != nil { //nolint:gosec // public cert
		return "", err
	}
	if err := cfg.saveIdentity(Identity{AgentID: issued.AgentID}); err != nil {
		return "", err
	}

	return issued.AgentID, nil
}
