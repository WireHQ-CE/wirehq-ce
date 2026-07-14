package agent

import (
	"crypto/ecdsa"
	"crypto/sha512"
	"crypto/x509"
	"encoding/base64"
	"encoding/pem"
	"errors"
	"fmt"
	"math/big"
)

// VerifyBundle checks that a deployment bundle was signed by the org CA before it is ever applied — so a
// breached transport cannot inject config. WireHQ signs with the org CA key (ECDSA-P384/SHA-384); the
// signature is .NET's IEEE-P1363 fixed-field format (r‖s), so we split it rather than expecting ASN.1 DER.
func VerifyBundle(caCertPEM, bundle []byte, signatureBase64 string) error {
	block, _ := pem.Decode(caCertPEM)
	if block == nil {
		return errors.New("ca certificate is not valid PEM")
	}
	cert, err := x509.ParseCertificate(block.Bytes)
	if err != nil {
		return fmt.Errorf("parse ca certificate: %w", err)
	}
	pub, ok := cert.PublicKey.(*ecdsa.PublicKey)
	if !ok {
		return errors.New("ca certificate key is not ECDSA")
	}

	sig, err := base64.StdEncoding.DecodeString(signatureBase64)
	if err != nil {
		return fmt.Errorf("decode signature: %w", err)
	}
	if len(sig) == 0 || len(sig)%2 != 0 {
		return errors.New("malformed signature")
	}

	half := len(sig) / 2
	r := new(big.Int).SetBytes(sig[:half])
	s := new(big.Int).SetBytes(sig[half:])

	digest := sha512.Sum384(bundle)
	if !ecdsa.Verify(pub, digest[:], r, s) {
		return errors.New("bundle signature is invalid")
	}
	return nil
}
