package agent

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"math/big"
	"net/http"
	"net/http/httptest"
	"os"
	"sync"
	"testing"
	"time"
)

// fakeGateway is a TLS server that requests a client cert, like the real gateway: it records the fingerprint each
// request presented and signs rotation CSRs with its own CA.
type fakeGateway struct {
	t        *testing.T
	srv      *httptest.Server
	caCert   *x509.Certificate
	caKey    *ecdsa.PrivateKey
	caPEM    string
	lifetime time.Duration

	mu        sync.Mutex
	seen      []string // fingerprints presented, in order
	rotations int
	failNext  bool
}

func newFakeGateway(t *testing.T) *fakeGateway {
	t.Helper()
	caKey, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	tmpl := &x509.Certificate{
		SerialNumber:          big.NewInt(1),
		Subject:               pkix.Name{CommonName: "test-ca"},
		NotBefore:             time.Now().Add(-time.Hour),
		NotAfter:              time.Now().Add(24 * time.Hour),
		IsCA:                  true,
		BasicConstraintsValid: true,
		KeyUsage:              x509.KeyUsageCertSign | x509.KeyUsageDigitalSignature,
	}
	der, err := x509.CreateCertificate(rand.Reader, tmpl, tmpl, &caKey.PublicKey, caKey)
	if err != nil {
		t.Fatal(err)
	}
	caCert, _ := x509.ParseCertificate(der)
	g := &fakeGateway{
		t: t, caCert: caCert, caKey: caKey, lifetime: 45 * 24 * time.Hour,
		caPEM: string(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})),
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/agent/v1/heartbeat", func(w http.ResponseWriter, r *http.Request) {
		g.record(r)
		_, _ = w.Write([]byte(`{"status":"ok"}`))
	})
	mux.HandleFunc("/agent/v1/cert/rotate", func(w http.ResponseWriter, r *http.Request) {
		g.record(r)
		g.mu.Lock()
		fail := g.failNext
		g.failNext = false
		g.rotations++
		g.mu.Unlock()
		if fail {
			http.Error(w, "boom", http.StatusInternalServerError)
			return
		}
		var req rotateRequest
		_ = json.NewDecoder(r.Body).Decode(&req)
		block, _ := pem.Decode([]byte(req.Csr))
		csr, err := x509.ParseCertificateRequest(block.Bytes)
		if err != nil {
			http.Error(w, "bad csr", http.StatusBadRequest)
			return
		}
		certPEM := g.issue(csr.PublicKey, time.Now().Add(-5*time.Minute), g.lifetime)
		_ = json.NewEncoder(w).Encode(rotateResponse{CertificatePEM: certPEM, CaCertificatePEM: g.caPEM})
	})

	g.srv = httptest.NewUnstartedServer(mux)
	g.srv.TLS = &tls.Config{ClientAuth: tls.RequireAnyClientCert}
	g.srv.StartTLS()
	t.Cleanup(g.srv.Close)
	return g
}

func (g *fakeGateway) record(r *http.Request) {
	g.mu.Lock()
	defer g.mu.Unlock()
	if len(r.TLS.PeerCertificates) > 0 {
		sum := sha256.Sum256(r.TLS.PeerCertificates[0].Raw)
		g.seen = append(g.seen, hex.EncodeToString(sum[:]))
	}
}

func (g *fakeGateway) lastSeen() string {
	g.mu.Lock()
	defer g.mu.Unlock()
	if len(g.seen) == 0 {
		return ""
	}
	return g.seen[len(g.seen)-1]
}

func (g *fakeGateway) rotationCount() int {
	g.mu.Lock()
	defer g.mu.Unlock()
	return g.rotations
}

func (g *fakeGateway) issue(pub any, notBefore time.Time, lifetime time.Duration) string {
	g.t.Helper()
	serial, _ := rand.Int(rand.Reader, big.NewInt(1<<62))
	tmpl := &x509.Certificate{
		SerialNumber: serial,
		Subject:      pkix.Name{CommonName: "agent"},
		NotBefore:    notBefore,
		NotAfter:     notBefore.Add(lifetime),
		KeyUsage:     x509.KeyUsageDigitalSignature,
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageClientAuth},
	}
	der, err := x509.CreateCertificate(rand.Reader, tmpl, g.caCert, pub, g.caKey)
	if err != nil {
		g.t.Fatal(err)
	}
	return string(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}))
}

// enrolledState writes an enrolled identity whose cert was issued `age` ago, returning the config + fingerprint.
func (g *fakeGateway) enrolledState(age time.Duration) (Config, string) {
	g.t.Helper()
	cfg := Config{Server: g.srv.URL, StateDir: g.t.TempDir(), InsecureSkipVerify: true}
	key, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	keyDER, _ := x509.MarshalPKCS8PrivateKey(key)
	certPEM := g.issue(&key.PublicKey, time.Now().Add(-age), g.lifetime)
	mustWrite(g.t, cfg.keyPath(), pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: keyDER}))
	mustWrite(g.t, cfg.certPath(), []byte(certPEM))
	mustWrite(g.t, cfg.caPath(), []byte(g.caPEM))
	return cfg, fingerprintOfFile(g.t, cfg.certPath())
}

func mustWrite(t *testing.T, path string, data []byte) {
	t.Helper()
	if err := os.WriteFile(path, data, 0o600); err != nil {
		t.Fatal(err)
	}
}

func fingerprintOfFile(t *testing.T, path string) string {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	block, _ := pem.Decode(data)
	sum := sha256.Sum256(block.Bytes)
	return hex.EncodeToString(sum[:])
}

const day = 24 * time.Hour

func TestRotationDueAtTwoThirdsOfLifetime(t *testing.T) {
	start := time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)
	leaf := &x509.Certificate{NotBefore: start, NotAfter: start.Add(45 * day)}

	if rotationDue(leaf, start.Add(29*day)) {
		t.Fatal("day 29 of 45 should not be due")
	}
	if !rotationDue(leaf, start.Add(30*day)) {
		t.Fatal("day 30 of 45 (two-thirds) should be due")
	}
	if !rotationDue(leaf, start.Add(44*day)) {
		t.Fatal("day 44 of 45 should be due")
	}
}

func TestADueCertificateIsRenewedPersistedAndPresentedWithoutARestart(t *testing.T) {
	g := newFakeGateway(t)
	cfg, oldFP := g.enrolledState(31 * day)
	loop, err := NewLoop(cfg)
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()

	// Warm a keep-alive connection on the old cert first: the switch must not keep reusing it.
	if err := loop.client.Heartbeat(ctx, Version); err != nil {
		t.Fatal(err)
	}
	if g.lastSeen() != oldFP {
		t.Fatal("expected the old cert before rotation")
	}

	loop.maybeRotate(ctx, time.Now())

	newFP := fingerprintOfFile(t, cfg.certPath())
	if newFP == oldFP {
		t.Fatal("the cert on disk was not replaced")
	}
	if _, err := tls.LoadX509KeyPair(cfg.certPath(), cfg.keyPath()); err != nil {
		t.Fatalf("persisted key + cert do not form a pair: %v", err)
	}
	for _, leftover := range []string{nextPath(cfg.keyPath()), nextPath(cfg.certPath())} {
		if _, err := os.Stat(leftover); !os.IsNotExist(err) {
			t.Fatalf("staging file %s left behind", leftover)
		}
	}

	// The very next request presents the new cert — this heartbeat is what ends the gateway's grace.
	if err := loop.client.Heartbeat(ctx, Version); err != nil {
		t.Fatal(err)
	}
	if g.lastSeen() != newFP {
		t.Fatal("the client kept presenting the old certificate after rotating")
	}

	// A fresh cert is not due again.
	loop.maybeRotate(ctx, time.Now())
	if g.rotationCount() != 1 {
		t.Fatalf("expected exactly one rotation, got %d", g.rotationCount())
	}
}

func TestAFreshCertificateIsNotRotated(t *testing.T) {
	g := newFakeGateway(t)
	cfg, _ := g.enrolledState(day)
	loop, err := NewLoop(cfg)
	if err != nil {
		t.Fatal(err)
	}
	loop.maybeRotate(context.Background(), time.Now())
	if g.rotationCount() != 0 {
		t.Fatal("a 1-day-old cert must not be rotated")
	}
}

func TestIfTheNewIdentityCannotBePersistedTheClientKeepsTheOldCertificate(t *testing.T) {
	g := newFakeGateway(t)
	cfg, oldFP := g.enrolledState(31 * day)
	loop, err := NewLoop(cfg)
	if err != nil {
		t.Fatal(err)
	}
	// A directory where the staging cert would go makes the write fail after the gateway has issued a cert.
	if err := os.Mkdir(nextPath(cfg.certPath()), 0o700); err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()

	loop.maybeRotate(ctx, time.Now())

	if g.rotationCount() != 1 {
		t.Fatal("expected the rotation to have been attempted")
	}
	if fingerprintOfFile(t, cfg.certPath()) != oldFP {
		t.Fatal("the cert on disk must be untouched")
	}
	// Presenting the unsaved cert would end the gateway's grace for the only cert on disk.
	if err := loop.client.Heartbeat(ctx, Version); err != nil {
		t.Fatal(err)
	}
	if g.lastSeen() != oldFP {
		t.Fatal("the client switched to a certificate that was never persisted")
	}
}

func TestAFailedRotationBacksOffInsteadOfRetryingEveryPoll(t *testing.T) {
	g := newFakeGateway(t)
	cfg, _ := g.enrolledState(31 * day)
	loop, err := NewLoop(cfg)
	if err != nil {
		t.Fatal(err)
	}
	g.mu.Lock()
	g.failNext = true
	g.mu.Unlock()
	now := time.Now()
	ctx := context.Background()

	loop.maybeRotate(ctx, now)
	loop.maybeRotate(ctx, now.Add(30*time.Second))
	if g.rotationCount() != 1 {
		t.Fatalf("expected a single attempt inside the back-off, got %d", g.rotationCount())
	}

	loop.maybeRotate(ctx, now.Add(rotateRetryBackoff+time.Second))
	if g.rotationCount() != 2 {
		t.Fatal("expected a retry once the back-off elapsed")
	}
}

func TestAnExpiredCertificateIsNotSentToTheGateway(t *testing.T) {
	g := newFakeGateway(t)
	cfg, _ := g.enrolledState(46 * day)
	loop, err := NewLoop(cfg)
	if err != nil {
		t.Fatal(err)
	}
	loop.maybeRotate(context.Background(), time.Now())
	if g.rotationCount() != 0 {
		t.Fatal("an expired cert cannot authenticate a rotation; it must not try")
	}
}

// ---- crash recovery ----

// stagedRotation leaves a valid new key + cert in the .next staging files, as a crash mid-persist would.
func stagedRotation(t *testing.T, g *fakeGateway, cfg Config) (keyPEM, certPEM []byte) {
	t.Helper()
	key, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	keyDER, _ := x509.MarshalPKCS8PrivateKey(key)
	keyPEM = pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: keyDER})
	certPEM = []byte(g.issue(&key.PublicKey, time.Now(), g.lifetime))
	mustWrite(t, nextPath(cfg.keyPath()), keyPEM)
	mustWrite(t, nextPath(cfg.certPath()), certPEM)
	return keyPEM, certPEM
}

func TestRecoveryPromotesAStagedIdentityInterruptedBeforeAnyRename(t *testing.T) {
	g := newFakeGateway(t)
	cfg, _ := g.enrolledState(31 * day)
	_, certPEM := stagedRotation(t, g, cfg)

	if err := recoverPendingRotation(cfg); err != nil {
		t.Fatal(err)
	}
	got, _ := os.ReadFile(cfg.certPath())
	if string(got) != string(certPEM) {
		t.Fatal("the staged cert was not promoted")
	}
	if _, err := tls.LoadX509KeyPair(cfg.certPath(), cfg.keyPath()); err != nil {
		t.Fatalf("recovered identity does not load: %v", err)
	}
}

func TestRecoveryFinishesARotationInterruptedBetweenTheRenames(t *testing.T) {
	g := newFakeGateway(t)
	cfg, _ := g.enrolledState(31 * day)
	stagedRotation(t, g, cfg)
	// The key rename happened; the cert rename did not — the live key and cert no longer match.
	if err := os.Rename(nextPath(cfg.keyPath()), cfg.keyPath()); err != nil {
		t.Fatal(err)
	}
	if loadsAsPair(cfg.certPath(), cfg.keyPath()) {
		t.Fatal("precondition: the live pair should be mismatched")
	}

	if err := recoverPendingRotation(cfg); err != nil {
		t.Fatal(err)
	}
	if !loadsAsPair(cfg.certPath(), cfg.keyPath()) {
		t.Fatal("recovery left a mismatched key + cert")
	}
	if _, err := NewLoop(cfg); err != nil {
		t.Fatalf("the agent cannot start after recovery: %v", err)
	}
}

func TestRecoveryDiscardsATornStagingWriteAndKeepsTheLiveIdentity(t *testing.T) {
	g := newFakeGateway(t)
	cfg, oldFP := g.enrolledState(31 * day)
	stagedRotation(t, g, cfg)
	mustWrite(t, nextPath(cfg.certPath()), []byte("-----BEGIN CERTIFICATE-----\ntrunc"))

	if err := recoverPendingRotation(cfg); err != nil {
		t.Fatal(err)
	}
	if fingerprintOfFile(t, cfg.certPath()) != oldFP {
		t.Fatal("the live cert must be kept when the staged one is torn")
	}
	for _, leftover := range []string{nextPath(cfg.keyPath()), nextPath(cfg.certPath())} {
		if _, err := os.Stat(leftover); !os.IsNotExist(err) {
			t.Fatalf("torn staging file %s was not discarded", leftover)
		}
	}
}

func TestARunningAgentAdoptsAnIdentityReKeyedOnDiskByAnotherProcess(t *testing.T) {
	g := newFakeGateway(t)
	cfg, oldFP := g.enrolledState(46 * day) // expired: the gateway would refuse it
	loop, err := NewLoop(cfg)
	if err != nil {
		t.Fatal(err)
	}

	// An operator re-keys while the service is running: `wirehq-agent enroll` rewrites the identity on disk.
	key, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	keyDER, _ := x509.MarshalPKCS8PrivateKey(key)
	mustWrite(t, cfg.keyPath(), pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: keyDER}))
	mustWrite(t, cfg.certPath(), []byte(g.issue(&key.PublicKey, time.Now(), g.lifetime)))
	newFP := fingerprintOfFile(t, cfg.certPath())

	loop.Once(context.Background())

	if g.lastSeen() != newFP || newFP == oldFP {
		t.Fatal("the running agent kept presenting the certificate the re-key replaced")
	}
}

func TestAHalfWrittenIdentityOnDiskIsNotAdopted(t *testing.T) {
	g := newFakeGateway(t)
	cfg, oldFP := g.enrolledState(day)
	loop, err := NewLoop(cfg)
	if err != nil {
		t.Fatal(err)
	}
	// Only the key has been replaced so far — the pair no longer matches.
	key, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	keyDER, _ := x509.MarshalPKCS8PrivateKey(key)
	mustWrite(t, cfg.keyPath(), pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: keyDER}))

	loop.Once(context.Background())

	if g.lastSeen() != oldFP {
		t.Fatal("a mismatched key + cert pair must not be adopted")
	}
}
