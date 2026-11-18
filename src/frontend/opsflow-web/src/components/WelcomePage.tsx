import { Link } from 'react-router-dom'
import type { LoginUserResponse } from '../auth/contracts'
import { Roles } from '../auth/roles'
import './WelcomePage.css'

interface WelcomePageProps {
  user: LoginUserResponse
}

export function WelcomePage({ user }: WelcomePageProps) {
  const canContribute = user.roles.includes(Roles.Technician)

  const features = [
    {
      title: 'Projects',
      description: "Browse your organization's projects.",
    },
    {
      title: 'Documents',
      description: canContribute
        ? 'Upload documents to a project and review their extracted text.'
        : 'Read project documents and their extracted text.',
    },
    {
      title: 'Search',
      description:
        "Find relevant passages across a project's documents with hybrid keyword and semantic search.",
    },
    {
      title: 'Ask',
      description:
        "Ask a question and get an answer grounded in the project's documents, with source citations.",
    },
  ]

  return (
    <div className="welcome-page">
      <h2 className="welcome-heading">Welcome, {user.displayName}</h2>
      <p className="welcome-intro">
        {canContribute
          ? `You're signed in to ${user.organizationName}. Browse projects, add documents, and ask questions answered directly from their content.`
          : `You're signed in to ${user.organizationName} with read-only access. Explore project documents and ask questions answered directly from their content.`}
      </p>

      <ul className="welcome-features" aria-label="What you can do">
        {features.map(feature => (
          <li key={feature.title} className="welcome-feature">
            <h3 className="welcome-feature-title">{feature.title}</h3>
            <p className="welcome-feature-description">{feature.description}</p>
          </li>
        ))}
      </ul>

      <p className="welcome-next">Open a project to search its documents or ask a question.</p>
      <Link to="/projects" className="welcome-cta">
        Browse projects
      </Link>
    </div>
  )
}
