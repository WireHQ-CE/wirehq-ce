package agent

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha512"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/base64"
	"encoding/pem"
	"math/big"
	"testing"
	"time"
)

// signP1363 signs like .NET's ECDsa.SignData: an IEEE-P1363 fixed-field r‖s signature (not ASN.1 DER).
func signP1363(t *testing.T, key *ecdsa.PrivateKey, message []byte) string {
	t.Helper()
	digest := sha512.Sum384(message)
	r, s, err := ecdsa.Sign(rand.Reader, key, digest[:])
	if err != nil {
		t.Fatal(err)
	}
	size := (key.Curve.Params().BitSize + 7) / 8
	out := make([]byte, 2*size)
	r.FillBytes(out[:size])
	s.FillBytes(out[size:])
	return base64.StdEncoding.EncodeToString(out)
}

func testCA(t *testing.T) (*ecdsa.PrivateKey, []byte) {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P384(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	template := x509.Certificate{
		SerialNumber:          big.NewInt(1),
		Subject:               pkix.Name{CommonName: "test ca"},
		NotBefore:             time.Now().Add(-time.Hour),
		NotAfter:              time.Now().Add(time.Hour),
		IsCA:                  true,
		KeyUsage:              x509.KeyUsageCertSign,
		BasicConstraintsValid: true,
	}
	der, err := x509.CreateCertificate(rand.Reader, &template, &template, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	return key, pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})
}

func TestVerifyBundle_AcceptsAGenuineSignature(t *testing.T) {
	key, caPEM := testCA(t)
	bundle := []byte("[Interface]\nPrivateKey = abc\nListenPort = 51820\n")

	if err := VerifyBundle(caPEM, bundle, signP1363(t, key, bundle)); err != nil {
		t.Fatalf("expected a valid signature to verify, got: %v", err)
	}
}

func TestVerifyBundle_RejectsATamperedBundle(t *testing.T) {
	key, caPEM := testCA(t)
	bundle := []byte("[Interface]\nListenPort = 51820\n")
	sig := signP1363(t, key, bundle)

	if err := VerifyBundle(caPEM, append(bundle, '!'), sig); err == nil {
		t.Fatal("expected a tampered bundle to be rejected")
	}
}

func TestVerifyBundle_RejectsAnotherKeysSignature(t *testing.T) {
	_, caPEM := testCA(t)
	attacker, err := ecdsa.GenerateKey(elliptic.P384(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	bundle := []byte("malicious")

	if err := VerifyBundle(caPEM, bundle, signP1363(t, attacker, bundle)); err == nil {
		t.Fatal("expected a signature from a different key to be rejected")
	}
}
