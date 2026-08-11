interface UnavailableScreenProps {
  onRetry: () => void
}

export function UnavailableScreen({ onRetry }: UnavailableScreenProps) {
  return (
    <div
      style={{
        minHeight: '100vh',
        display: 'flex',
        flexDirection: 'column' as const,
        alignItems: 'center',
        justifyContent: 'center',
        gap: '1rem',
        padding: '1.5rem',
        textAlign: 'center' as const,
      }}
    >
      <h2 style={{ margin: 0, fontSize: '1.25rem', fontWeight: 600 }}>
        Service Unavailable
      </h2>
      <p
        style={{
          margin: 0,
          color: 'var(--text-muted)',
          maxWidth: '24rem',
        }}
      >
        The authentication service is temporarily unavailable. Please try
        again.
      </p>
      <button
        onClick={onRetry}
        type="button"
        style={{
          marginTop: '0.5rem',
          padding: '0.5rem 1rem',
          border: '1px solid var(--border)',
          borderRadius: '0.375rem',
          backgroundColor: 'var(--bg)',
          color: 'var(--text)',
          fontSize: '0.875rem',
          cursor: 'pointer',
        }}
      >
        Retry
      </button>
    </div>
  )
}
