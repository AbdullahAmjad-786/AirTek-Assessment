using System.Threading.Tasks;
using System.Collections.Generic;
using Pulumi;
using Aws = Pulumi.Aws;
using Pulumi.Awsx.Ec2.Inputs;
using Ec2 = Pulumi.Awsx.Ec2;
using Eks = Pulumi.Eks;
using Awsx = Pulumi.Awsx;
using Kubernetes = Pulumi.Kubernetes;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Networking.V1;
using Pulumi.Eks;
using nginx = Pulumi.KubernetesIngressNginx;

class Program
{
    static Task<int> Main()
    {
        return Deployment.RunAsync(() => {
            var config = new Config();
            var minClusterSize = config.GetInt32("minClusterSize");
            var maxClusterSize = config.GetInt32("maxClusterSize");
            var desiredClusterSize = config.GetInt32("desiredClusterSize");
            var eksNodeInstanceType = config.Get("eksNodeInstanceType");
            var vpcNetworkCidr = config.Get("vpcNetworkCidr");

            // Tags to apply to resources
            var tags = new InputMap<string>
            {
                { "Environment", "Production" },
                { "Project", "AirTek" },
            };

            // Create a new ECR
            var repo = new Awsx.Ecr.Repository("airTek-repo", new()
            {
                Tags = tags,
            });

            //Create & Push Image for Web App
            var appImage = new Awsx.Ecr.Image("web-app-image", new()
            {
                RepositoryUrl = repo.Url,
                Path = "../InfraWeb",
            });

            //Create & Push Image for Web Api
            var apiImage = new Awsx.Ecr.Image("web-api-image", new()
            {
                RepositoryUrl = repo.Url,
                Path = "../InfraApi",
            });

            // Create a new VPC
            var vpc = new Ec2.Vpc("airTek-vpc", new Ec2.VpcArgs
            {
                EnableDnsHostnames = true,
                CidrBlock = vpcNetworkCidr,
                Tags= tags,
                SubnetSpecs =
                {
                    new SubnetSpecArgs
                    {
                        Type = Ec2.SubnetType.Public,
                        CidrMask = 22,
                    },
                    new SubnetSpecArgs
                    {
                        Type = Ec2.SubnetType.Private,
                        CidrMask = 20,
                    }
                }
            });

            // Security Group for Eks Cluster
            var securityGroup = new Aws.Ec2.SecurityGroup("eks-sg", new Aws.Ec2.SecurityGroupArgs
            {
                Description = "Security Group for EKS Cluster",
                VpcId = vpc.VpcId,

            });

            // EKS cluster
            var cluster = new Eks.Cluster("airTek-cluster", new()
            {
                //VPC Configurations
                VpcId = vpc.VpcId,
                PublicSubnetIds = vpc.PublicSubnetIds,
                PrivateSubnetIds = vpc.PrivateSubnetIds,

                //Cluster Configurations
                InstanceType = eksNodeInstanceType,
                DesiredCapacity = desiredClusterSize,
                MinSize = minClusterSize,
                MaxSize = maxClusterSize,

                NodeAssociatePublicIpAddress = false,
                Tags= tags,
            });

            var eksProvider = new Kubernetes.Provider("eks-provider", new()
            {
                KubeConfig = cluster.KubeconfigJson,
            });

            // Node Security Group for Eks Cluster
            var nodeSecurityGroup = new Eks.NodeGroupSecurityGroup("eks-nsg", new NodeGroupSecurityGroupArgs
            {
                ClusterSecurityGroup = securityGroup,
                EksCluster = cluster.EksCluster,
                VpcId = vpc.VpcId,
                Tags = tags,
            }, new ComponentResourceOptions
            {
                Provider = eksProvider,
            }) ;

            // Kubernetes namespace for the web app and API
            var webAppNamespace = new Kubernetes.Core.V1.Namespace("airTek-namespace", new NamespaceArgs
            {
                Metadata = new ObjectMetaArgs
                {
                    Name = "airtek-prod",
                },
            }, new CustomResourceOptions
            {
                Provider = eksProvider,
            });

            // Kubernetes nginx ingress controller
            var nginxIngress = new nginx.IngressController("nginx-ingress", new nginx.IngressControllerArgs 
            {
                 Controller = new nginx.Inputs.ControllerArgs
                 {
                     PublishService = new nginx.Inputs.ControllerPublishServiceArgs
                     {
                         Enabled = true,
                     },
                 },
                 HelmOptions = new nginx.Inputs.ReleaseArgs
                 {
                     Namespace       = "ingress-controller",
                     CreateNamespace = true
                 },
            }, new ComponentResourceOptions
            {
                Provider = eksProvider,
            });

            // Deploy Web App
            var appDeployment = new Kubernetes.Apps.V1.Deployment("web-app", new()
            {
                Metadata = new ObjectMetaArgs
                {
                    Namespace = webAppNamespace.Metadata.Apply(meta => meta.Name),
                    Labels =
                    {
                        { "app", "web-app" },
                    },
                },
                Spec = new Kubernetes.Types.Inputs.Apps.V1.DeploymentSpecArgs
                {
                    Replicas = 1,
                    Selector = new LabelSelectorArgs
                    {
                        MatchLabels =
                        {
                            { "app", "web-app" },
                        },
                    },
                    Template = new PodTemplateSpecArgs
                    {
                        Metadata = new ObjectMetaArgs
                        {
                            Labels =
                            {
                                { "app", "web-app" },
                            },
                        },
                        Spec = new PodSpecArgs
                        {
                            Containers = new[]
                            {
                                new ContainerArgs
                                {
                                    Name = "web-app-container",
                                    Image = appImage.ImageUri,
                                    Env = new[]
                                    {
                                        new EnvVarArgs
                                        {
                                            Name= "ApiAddress",
                                            Value= "http://infra-api:80/WeatherForecast",
                                        },
                                    },
                                    Ports = new[]
                                    {
                                        new ContainerPortArgs
                                        {
                                            Name = "http",
                                            ContainerPortValue = 5000,
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            }, new CustomResourceOptions
            {
                Provider = eksProvider,
            });

            // Deploy Web Api
            var apiDeployment = new Kubernetes.Apps.V1.Deployment("web-api", new()
            {
                Metadata = new ObjectMetaArgs
                {
                    Namespace = webAppNamespace.Metadata.Apply(meta => meta.Name),
                    Labels =
                    {
                        { "app", "web-api" },
                    },
                },
                Spec = new Kubernetes.Types.Inputs.Apps.V1.DeploymentSpecArgs
                {
                    Replicas = 1,
                    Selector = new LabelSelectorArgs
                    {
                        MatchLabels =
                        {
                            { "app", "web-api" },
                        },
                    },
                    Template = new PodTemplateSpecArgs
                    {
                        Metadata = new ObjectMetaArgs
                        {
                            Labels =
                            {
                                { "app", "web-api" },
                            },
                        },
                        Spec = new PodSpecArgs
                        {
                            Containers = new[]
                            {
                                new ContainerArgs
                                {
                                    Name = "web-api-container",
                                    Image = apiImage.ImageUri,
                                    Ports = new[]
                                    {
                                        new ContainerPortArgs
                                        {
                                            Name = "http",
                                            ContainerPortValue = 5000,
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            }, new CustomResourceOptions
            {
                Provider = eksProvider,
            });

            //Deploy Web App Service
            var webAppService = new Kubernetes.Core.V1.Service("web-app-service", new()
            {
                Metadata = new ObjectMetaArgs
                {
                    Name      = "infra-app",
                    Namespace = webAppNamespace.Metadata.Apply(meta => meta.Name),
                    Labels =
                    {
                        { "app", "web-app" },
                    },
                },
                Spec = new ServiceSpecArgs
                {
                    Ports = new[]
                    {
                        new ServicePortArgs
                        {
                            Port = 80,
                            TargetPort = 5000,
                        },
                    },
                    Selector =
                    {
                        { "app", "web-app" },
                    },
                },
            }, new CustomResourceOptions
            {
                Provider = eksProvider,
            });

            //Deploy Web Api Service
            var webApiService = new Kubernetes.Core.V1.Service("web-api-service", new()
            {
                Metadata = new ObjectMetaArgs
                {
                    Name      = "infra-api",
                    Namespace = webAppNamespace.Metadata.Apply(meta => meta.Name),
                    Labels =
                    {
                        { "app", "web-api" },
                    },
                },
                Spec = new ServiceSpecArgs
                {
                    Ports = new[]
                    {
                        new ServicePortArgs
                        {
                            Port = 80,
                            TargetPort = 5000,
                        },
                    },
                    Selector =
                    {
                        { "app", "web-api" },
                    },
                },
            }, new CustomResourceOptions
            {
                Provider = eksProvider,
            });

            // Ingress resource for the web app
            var webAppIngress = new Kubernetes.Networking.V1.Ingress("web-app-ingress", new()
            {
                Metadata = new ObjectMetaArgs
                {
                    Namespace = webAppNamespace.Metadata.Apply(meta => meta.Name),
                    Annotations =
                    {
                        { "kubernetes.io/ingress.class", "nginx" },
                    },
                },
                Spec = new IngressSpecArgs
                {
                    Rules = new[]
                    {
                        new IngressRuleArgs
                        {
                           // Host = "airtek-web-app.com",
                            Http = new HTTPIngressRuleValueArgs
                            {
                                Paths = new[]
                                {
                                    new HTTPIngressPathArgs
                                    {
                                        Backend = new IngressBackendArgs
                                        {
                                            Service = new IngressServiceBackendArgs
                                            {
                                                Name = webAppService.Metadata.Apply(meta => meta.Name),
                                                Port = new ServiceBackendPortArgs
                                                {
                                                    Number = webAppService.Spec.Apply(spec => spec.Ports[0].Port),
                                                },
                                            },
                                        },
                                        Path = "/",
                                        PathType = "Prefix",
                                    },
                                },
                            },
                        },
                    },
                },
            }, new CustomResourceOptions
            {
                Provider = eksProvider,
                DependsOn = nginxIngress,
            });

            // Export the ECR, EKS cluster information
            return new Dictionary<string, object?>
            {
                { "repositoryName", repo.Url },
                { "clusterName", cluster.GetResourceName() },
            };
        });
    }
}
