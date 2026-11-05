interface Props {
  className: string;
  children: string;
}

export function GoogleLoginLink({ className, children }: Props) {
  return (
    <a className={className} href="/api/v1/auth/login">
      <span className="google-mark" aria-hidden="true">
        <svg viewBox="0 0 24 24">
          <path
            fill="#4285F4"
            d="M21.35 12.24c0-.75-.07-1.47-.2-2.16H12v4.09h5.23a4.47 4.47 0 0 1-1.94 2.93v2.65h3.14c1.84-1.7 2.92-4.2 2.92-7.51Z"
          />
          <path
            fill="#34A853"
            d="M12 21.73c2.62 0 4.82-.87 6.43-2.35l-3.14-2.65c-.87.59-1.99.94-3.29.94-2.53 0-4.67-1.71-5.44-4.01H3.31v2.73A9.72 9.72 0 0 0 12 21.73Z"
          />
          <path
            fill="#FBBC05"
            d="M6.56 13.66A5.85 5.85 0 0 1 6.25 12c0-.58.1-1.14.31-1.66V7.61H3.31A9.73 9.73 0 0 0 2.27 12c0 1.57.37 3.05 1.04 4.39l3.25-2.73Z"
          />
          <path
            fill="#EA4335"
            d="M12 6.33c1.42 0 2.7.49 3.7 1.45l2.78-2.78C16.81 3.43 14.61 2.27 12 2.27a9.72 9.72 0 0 0-8.69 5.34l3.25 2.73c.77-2.3 2.91-4.01 5.44-4.01Z"
          />
        </svg>
      </span>
      {children}
    </a>
  );
}
